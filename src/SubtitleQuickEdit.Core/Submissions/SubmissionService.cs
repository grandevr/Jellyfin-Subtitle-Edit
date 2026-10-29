using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Submissions;

/// <summary>
/// Submit for review: users submit edits, which are checked against the file but not written; admins approve them
/// (written through <see cref="SubtitleSaveService"/>, so with the same hash check and backups as their own saves)
/// or reject them; users may withdraw their own pending ones. Only the newest <see cref="DecidedKept"/> decided
/// submissions are kept.
/// </summary>
public sealed class SubmissionService
{
    /// <summary>
    /// The longest note or rejection reason, in characters.
    /// </summary>
    public const int MaxTextLength = 500;

    /// <summary>
    /// How many decided or withdrawn submissions are kept; older ones are deleted.
    /// </summary>
    public const int DecidedKept = 50;

    private const string ConflictMessage = "The line was changed or removed after this was submitted.";

    private readonly SubmissionStore store;
    private readonly SubtitleSaveService saver;
    private readonly TimeProvider clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubmissionService"/> class.
    /// </summary>
    /// <param name="store">Where submissions are kept.</param>
    /// <param name="saver">Writes approved edits.</param>
    /// <param name="clock">The clock; the system clock when null.</param>
    public SubmissionService(SubmissionStore store, SubtitleSaveService saver, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(saver);
        this.store = store;
        this.saver = saver;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Reads every submission, newest first.
    /// </summary>
    /// <returns>The submissions.</returns>
    public IReadOnlyList<Submission> All() => store.All();

    /// <summary>
    /// Reads one submission.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <returns>The submission, or null.</returns>
    public Submission? Find(string id) => store.Find(id);

    /// <summary>
    /// Checks edits against the file, exactly as a save would, and stores them as a pending submission. The file is
    /// not changed.
    /// </summary>
    /// <param name="request">What to submit.</param>
    /// <returns>The stored submission.</returns>
    /// <exception cref="SubtitleNotEditableException">The file cannot be edited.</exception>
    /// <exception cref="SubtitleEditException">An edit is invalid or conflicts with the file's text.</exception>
    /// <exception cref="SubtitleWriteException">The file changed since the user loaded it (a conflict).</exception>
    /// <exception cref="SubmissionException">The note is too long, the user has too many pending, or storing failed.</exception>
    public Submission Submit(SubmitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Edits.Count == 0)
        {
            throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "There are no changes to submit.");
        }

        string? note = CheckText(request.Note, "note");
        ISubtitleDocument doc = Open(request.Path);
        string baseHash = request.BaseHash.Trim().ToLowerInvariant();
        if (!string.Equals(doc.Hash, baseHash, StringComparison.Ordinal))
        {
            throw new SubtitleWriteException(
                SubtitleWriteError.Conflict,
                "The subtitle file changed on disk since it was loaded.",
                currentHash: doc.Hash);
        }

        // The same checks as a save; the result is thrown away.
        doc.Apply(request.Edits, request.RuntimeMs);
        var submission = new Submission
        {
            Id = SubmissionStore.NewId(),
            UserId = request.UserId,
            UserName = request.UserName,
            ItemId = request.ItemId.ToLowerInvariant(),
            StreamIndex = request.StreamIndex,
            BaseHash = baseHash,
            Edits = request.Edits.Select(e => Describe(doc, e)).ToList(),
            Note = note,
            CreatedUtc = clock.GetUtcNow(),
            State = SubmissionState.Pending,
        };

        lock (store.SyncRoot)
        {
            int pending = store.All().Count(s => s.State == SubmissionState.Pending
                && string.Equals(s.UserId, request.UserId, StringComparison.OrdinalIgnoreCase));
            if (pending >= request.MaxPending)
            {
                throw new SubmissionException(
                    SubmissionError.TooManyPending,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"You already have {pending} submissions waiting for review. Wait until an admin has decided some of them."));
            }

            store.Save(submission);
        }

        return submission;
    }

    /// <summary>
    /// Takes back a pending submission of the caller's.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <param name="userId">The caller's Jellyfin id; must be the submitter's.</param>
    /// <param name="userName">The caller's name, kept with the withdrawal.</param>
    /// <returns>The withdrawn submission.</returns>
    /// <exception cref="SubmissionException">No such submission of the caller's, or it is no longer pending.</exception>
    public Submission Withdraw(string id, string userId, string userName)
    {
        lock (store.SyncRoot)
        {
            Submission submission = store.Find(id) ?? throw NotFound();
            if (!string.Equals(submission.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                throw NotFound();
            }

            RequirePending(submission);
            return Decide(submission with
            {
                State = SubmissionState.Withdrawn,
                DecidedUtc = clock.GetUtcNow(),
                DecidedBy = userName,
            });
        }
    }

    /// <summary>
    /// Rejects a pending submission. Nothing is written to the subtitle.
    /// </summary>
    /// <param name="id">The submission id.</param>
    /// <param name="adminName">The admin's name, kept with the decision.</param>
    /// <param name="reason">An optional reason shown to the user.</param>
    /// <returns>The rejected submission.</returns>
    /// <exception cref="SubmissionException">No such submission, it is no longer pending, or the reason is too long.</exception>
    public Submission Reject(string id, string adminName, string? reason)
    {
        string? why = CheckText(reason, "reason");
        lock (store.SyncRoot)
        {
            Submission submission = store.Find(id) ?? throw NotFound();
            RequirePending(submission);
            return Decide(submission with
            {
                State = SubmissionState.Rejected,
                DecidedUtc = clock.GetUtcNow(),
                DecidedBy = adminName,
                Reason = why,
            });
        }
    }

    /// <summary>
    /// Approves a pending submission: writes the chosen edits in one save. When the file changed since it was
    /// submitted, each text edit is looked for again (same position and text, else the one line with the same start
    /// time and text); edits not found are conflicts and are not written. New lines are always written. When no chosen
    /// edit can be written, nothing is written and the submission stays pending with its conflicts marked.
    /// </summary>
    /// <param name="request">What to approve.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="SubmissionException">No such submission, it is no longer pending, or the choice is invalid.</exception>
    /// <exception cref="SubtitleNotEditableException">The file cannot be edited any more.</exception>
    /// <exception cref="SubtitleEditException">An edit is no longer valid for the file. Nothing was written.</exception>
    /// <exception cref="SubtitleWriteException">Writing failed. Nothing was written; the submission stays pending.</exception>
    public ApproveResult Approve(ApproveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (store.SyncRoot)
        {
            Submission submission = store.Find(request.Id) ?? throw NotFound();
            RequirePending(submission);
            HashSet<int> chosen = Choose(submission, request.Chosen);
            ISubtitleDocument doc = Open(request.Path);
            bool unchanged = string.Equals(doc.Hash, submission.BaseHash, StringComparison.Ordinal);
            var outcomes = new List<EditOutcome>(submission.Edits.Count);
            var toApply = new List<SubtitleEdit>();
            for (int i = 0; i < submission.Edits.Count; i++)
            {
                SubtitleEdit? edit = chosen.Contains(i) ? Resolve(doc, submission.Edits[i], unchanged) : null;
                if (edit is not null)
                {
                    toApply.Add(edit);
                }

                outcomes.Add(!chosen.Contains(i) ? EditOutcome.NotChosen : edit is null ? EditOutcome.Conflict : EditOutcome.Applied);
            }

            if (toApply.Count == 0)
            {
                Submission marked = submission with { Edits = Mark(submission.Edits, outcomes, conflictsOnly: true) };
                store.Save(marked);
                return new ApproveResult(marked, null);
            }

            SaveResult saved = saver.Save(
                new SaveRequest(submission.ItemId, submission.StreamIndex, request.Path, doc.Hash, toApply, request.RuntimeMs));
            bool all = outcomes.TrueForAll(o => o == EditOutcome.Applied);
            Submission decided = Decide(submission with
            {
                Edits = Mark(submission.Edits, outcomes, conflictsOnly: false),
                State = all ? SubmissionState.Approved : SubmissionState.PartlyApproved,
                DecidedUtc = clock.GetUtcNow(),
                DecidedBy = request.AdminName,
            });
            return new ApproveResult(decided, saved);
        }
    }

    private static ISubtitleDocument Open(string path)
    {
        TrackInspection inspection = SubtitleTrackInspector.Inspect(path);
        return inspection.Document ?? throw new SubtitleNotEditableException(inspection.Reason ?? "The subtitle cannot be edited.");
    }

    private static SubmittedEdit Describe(ISubtitleDocument doc, SubtitleEdit edit) => edit switch
    {
        SubtitleTextEdit text => new SubmittedEdit
        {
            Kind = SubmittedEditKind.Edit,
            CuePosition = text.CuePosition,
            CueStartMs = doc.Cues[text.CuePosition - 1].StartMs,
            CueEndMs = doc.Cues[text.CuePosition - 1].EndMs,
            NewStartMs = text.NewStartMs,
            NewEndMs = text.NewEndMs,
            OldText = doc.Cues[text.CuePosition - 1].Text,
            NewText = SubtitleText.NormalizeBreaks(text.NewText),
        },
        SubtitleAddCue add => new SubmittedEdit
        {
            Kind = SubmittedEditKind.Add,
            ClientId = add.ClientId,
            StartMs = add.StartMs,
            EndMs = add.EndMs,
            Text = SubtitleText.NormalizeBreaks(add.Text),
        },
        _ => throw new SubtitleEditException(SubtitleEditError.InvalidEdit, "Unknown edit type."),
    };

    // A stored edit as one to save, or null when it is a text edit whose line can no longer be found.
    private static SubtitleEdit? Resolve(ISubtitleDocument doc, SubmittedEdit edit, bool unchanged)
    {
        if (edit.Kind == SubmittedEditKind.Add)
        {
            return new SubtitleAddCue(edit.ClientId ?? string.Empty, edit.StartMs ?? 0, edit.EndMs ?? 0, edit.Text ?? string.Empty);
        }

        string oldText = edit.OldText ?? string.Empty;
        int? position = unchanged ? edit.CuePosition : FindLine(doc, edit, oldText);
        return position is int p
            ? new SubtitleTextEdit(p, oldText, edit.NewText ?? string.Empty) { NewStartMs = edit.NewStartMs, NewEndMs = edit.NewEndMs }
            : null;
    }

    private static int? FindLine(ISubtitleDocument doc, SubmittedEdit edit, string oldText)
    {
        // New times replace the line's times, so the line must still have the times the user saw.
        bool retimed = edit.NewStartMs.HasValue && edit.CueEndMs.HasValue;
        bool Same(SubtitleCue c) => string.Equals(c.Text, oldText, StringComparison.Ordinal)
            && (!retimed || (c.StartMs == edit.CueStartMs && c.EndMs == edit.CueEndMs));

        int p = edit.CuePosition ?? 0;
        if (p >= 1 && p <= doc.Cues.Count && Same(doc.Cues[p - 1]))
        {
            return p;
        }

        List<SubtitleCue> same = doc.Cues
            .Where(c => c.StartMs == edit.CueStartMs && Same(c))
            .ToList();
        return same.Count == 1 ? same[0].Position : null;
    }

    private static HashSet<int> Choose(Submission submission, IReadOnlyList<int>? chosen)
    {
        if (chosen is null)
        {
            return Enumerable.Range(0, submission.Edits.Count).ToHashSet();
        }

        var set = chosen.ToHashSet();
        if (chosen.Count == 0 || set.Count != chosen.Count || set.Any(i => i < 0 || i >= submission.Edits.Count))
        {
            throw new SubmissionException(SubmissionError.InvalidChoice, "Choose one or more of the submission's changes, each once.");
        }

        return set;
    }

    private static List<SubmittedEdit> Mark(IReadOnlyList<SubmittedEdit> edits, List<EditOutcome> outcomes, bool conflictsOnly) =>
        edits.Select((e, i) =>
        {
            EditOutcome outcome = conflictsOnly && outcomes[i] != EditOutcome.Conflict ? EditOutcome.Pending : outcomes[i];
            return e with { Outcome = outcome, Message = outcome == EditOutcome.Conflict ? ConflictMessage : null };
        }).ToList();

    private static string? CheckText(string? value, string what)
    {
        string? text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxTextLength)
        {
            throw new SubmissionException(
                SubmissionError.TextTooLong,
                string.Create(CultureInfo.InvariantCulture, $"The {what} can be at most {MaxTextLength} characters."));
        }

        return text;
    }

    private static void RequirePending(Submission submission)
    {
        if (submission.State != SubmissionState.Pending)
        {
            throw new SubmissionException(SubmissionError.NotPending, "This submission was already decided or withdrawn.");
        }
    }

    private static SubmissionException NotFound() => new(SubmissionError.NotFound, "No such submission.");

    private Submission Decide(Submission decided)
    {
        store.Save(decided);
        store.PruneDecided(DecidedKept);
        return decided;
    }
}
