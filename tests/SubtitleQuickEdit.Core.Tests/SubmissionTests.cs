using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Submissions;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SubmissionTests
{
    private const string Item = "55de67aa9c161f4100fce9da37b4b0cb";
    private const string User = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string OtherUser = "ffeeddccbbaa99887766554433221100";

    private static byte[] Original() => Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));

    private static string Text(int position) => Fixtures.English[position - 1];

    private sealed class Setup : IDisposable
    {
        private readonly TempDir dir = new();

        public Setup(IWriteObserver? observer = null)
        {
            Directory.CreateDirectory(Path.Combine(dir.Root, "media"));
            SubPath = Path.Combine(dir.Root, "media", "movie.en.srt");
            File.WriteAllBytes(SubPath, Original());
            Store = new SubmissionStore(Path.Combine(dir.Root, "submissions"));
            Saver = new SubtitleSaveService(
                new SafeFileWriter(null, observer), new BackupStore(Path.Combine(dir.Root, "backups"), BackupMode.OriginalOnly, 3));
            Clock = new ManualClock();
            Service = new SubmissionService(Store, Saver, Clock);
        }

        public string SubPath { get; }

        public SubmissionStore Store { get; }

        public SubtitleSaveService Saver { get; }

        public ManualClock Clock { get; }

        public SubmissionService Service { get; }

        public string Root => dir.Root;

        public string Hash => Content.Hash(File.ReadAllBytes(SubPath));

        public SrtDocument Doc => SrtDocument.Parse(File.ReadAllBytes(SubPath)).Document!;

        public Submission Submit(string user, params SrtEdit[] edits) => Submit(user, null, 50, edits);

        public Submission Submit(string user, string? note, int maxPending, params SrtEdit[] edits) =>
            Service.Submit(new SubmitRequest(user, "name-" + user[..4], Item, 3, SubPath, Hash, edits, note, 600_000, maxPending));

        public ApproveResult Approve(string id, params int[] chosen) =>
            Service.Approve(new ApproveRequest(id, SubPath, "admin", chosen.Length == 0 ? null : chosen, 600_000));

        public void Dispose() => dir.Dispose();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            now = now.AddSeconds(1);
            return now;
        }
    }

    [Fact]
    public void Submit_checks_the_edits_stores_them_and_leaves_the_file_alone()
    {
        using var s = new Setup();
        byte[] before = File.ReadAllBytes(s.SubPath);

        Submission sub = s.Submit(User, "  typo fix  ", 50, new SrtTextEdit(3, Text(3), "Did you hear that?!"), new SrtAddCue("n1", 500, 1500, "Intro"));

        Assert.Equal(before, File.ReadAllBytes(s.SubPath));
        Submission stored = Assert.Single(s.Store.All());
        Assert.Equal(sub, stored with { Edits = sub.Edits });
        Assert.Equal(SubmissionState.Pending, stored.State);
        Assert.Equal("typo fix", stored.Note);
        Assert.Equal(Content.Hash(before), stored.BaseHash);
        Assert.Equal(2, stored.Edits.Count);
        SubmittedEdit edit = stored.Edits[0];
        Assert.Equal(SubmittedEditKind.Edit, edit.Kind);
        Assert.Equal(3, edit.CuePosition);
        Assert.Equal(11000L, edit.CueStartMs);
        Assert.Equal(Text(3), edit.OldText);
        Assert.Equal("Did you hear that?!", edit.NewText);
        SubmittedEdit add = stored.Edits[1];
        Assert.Equal(SubmittedEditKind.Add, add.Kind);
        Assert.Equal("n1", add.ClientId);
        Assert.Equal(500, add.StartMs);
        Assert.Equal(1500, add.EndMs);
        Assert.Equal("Intro", add.Text);
        Assert.Equal(stored, s.Store.Find(sub.Id)! with { Edits = stored.Edits });
        Assert.False(Directory.Exists(Path.Combine(s.Root, "backups")));
    }

    [Fact]
    public void Submit_refuses_what_a_save_would_refuse_and_stores_nothing()
    {
        using var s = new Setup();
        string hash = s.Hash;

        var mismatch = Assert.Throws<SrtEditException>(() => s.Submit(User, new SrtTextEdit(2, "not the text", "x")));
        Assert.Equal(SrtEditError.TextMismatch, mismatch.Error);
        var empty = Assert.Throws<SrtEditException>(() => s.Submit(User));
        Assert.Equal(SrtEditError.InvalidEdit, empty.Error);
        var stale = Assert.Throws<SubtitleWriteException>(() => s.Service.Submit(new SubmitRequest(
            User, "u", Item, 3, s.SubPath, new string('0', 64), new SrtEdit[] { new SrtTextEdit(1, Text(1), "x") }, null, null, 50)));
        Assert.Equal(SubtitleWriteError.Conflict, stale.Error);
        var longNote = Assert.Throws<SubmissionException>(() => s.Submit(User, new string('a', 501), 50, new SrtTextEdit(1, Text(1), "x")));
        Assert.Equal(SubmissionError.TextTooLong, longNote.Error);

        Assert.Empty(s.Store.All());
        Assert.Equal(hash, s.Hash);
    }

    [Fact]
    public void A_user_with_too_many_pending_submissions_is_refused_and_withdrawn_ones_do_not_count()
    {
        using var s = new Setup();
        Submission first = s.Submit(User, null, 2, new SrtTextEdit(1, Text(1), "a"));
        s.Submit(User, null, 2, new SrtTextEdit(2, Text(2), "b"));

        var refused = Assert.Throws<SubmissionException>(() => s.Submit(User, null, 2, new SrtTextEdit(3, Text(3), "c")));
        Assert.Equal(SubmissionError.TooManyPending, refused.Error);
        s.Submit(OtherUser, null, 2, new SrtTextEdit(3, Text(3), "c"));
        s.Service.Withdraw(first.Id, User, "name");
        s.Submit(User, null, 2, new SrtTextEdit(3, Text(3), "c"));

        Assert.Equal(4, s.Store.All().Count);
    }

    [Fact]
    public void Only_the_submitter_can_withdraw_and_only_while_pending()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "a"));

        var notTheirs = Assert.Throws<SubmissionException>(() => s.Service.Withdraw(sub.Id, OtherUser, "other"));
        Assert.Equal(SubmissionError.NotFound, notTheirs.Error);
        Submission withdrawn = s.Service.Withdraw(sub.Id, User.ToUpperInvariant(), "name");

        Assert.Equal(SubmissionState.Withdrawn, withdrawn.State);
        Assert.Equal("name", withdrawn.DecidedBy);
        Assert.NotNull(withdrawn.DecidedUtc);
        Assert.Equal(SubmissionState.Withdrawn, s.Store.Find(sub.Id)!.State);
        Assert.Equal(SubmissionError.NotPending, Assert.Throws<SubmissionException>(() => s.Service.Withdraw(sub.Id, User, "name")).Error);
        Assert.Equal(SubmissionError.NotPending, Assert.Throws<SubmissionException>(() => s.Approve(sub.Id)).Error);
        Assert.Equal(SubmissionError.NotFound, Assert.Throws<SubmissionException>(() => s.Service.Withdraw("nope", User, "name")).Error);
    }

    [Fact]
    public void Reject_keeps_the_reason_and_writes_nothing()
    {
        using var s = new Setup();
        string hash = s.Hash;
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "a"));

        Submission rejected = s.Service.Reject(sub.Id, "admin", " Not a typo. ");

        Assert.Equal(SubmissionState.Rejected, rejected.State);
        Assert.Equal("Not a typo.", rejected.Reason);
        Assert.Equal("admin", rejected.DecidedBy);
        Assert.Equal(hash, s.Hash);
        Assert.Equal(SubmissionError.NotPending, Assert.Throws<SubmissionException>(() => s.Service.Reject(sub.Id, "admin", null)).Error);
        Assert.Equal(
            SubmissionError.TextTooLong,
            Assert.Throws<SubmissionException>(() => s.Service.Reject(s.Submit(User, new SrtTextEdit(2, Text(2), "b")).Id, "admin", new string('r', 501))).Error);
    }

    [Fact]
    public void Approving_an_unchanged_file_writes_every_edit_with_a_backup()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(3, Text(3), "Did you hear that?!"), new SrtAddCue("n1", 500, 1500, "Intro"));

        ApproveResult result = s.Approve(sub.Id);

        Assert.NotNull(result.Save);
        Assert.Equal(result.Save!.Hash, s.Hash);
        SrtDocument doc = s.Doc;
        Assert.Equal("Intro", doc.Cues[0].Text);
        Assert.Equal("Did you hear that?!", doc.Cues[3].Text);
        Assert.Equal(SubmissionState.Approved, result.Submission.State);
        Assert.All(result.Submission.Edits, e => Assert.Equal(EditOutcome.Applied, e.Outcome));
        Assert.Equal("admin", result.Submission.DecidedBy);
        Assert.Equal(result.Submission, s.Store.Find(sub.Id)! with { Edits = result.Submission.Edits });
        Assert.Single(Directory.GetFiles(Path.Combine(s.Root, "backups")));
    }

    [Fact]
    public void Approving_selected_edits_writes_only_those_and_marks_the_rest_not_chosen()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "First"), new SrtTextEdit(2, Text(2), "Second"));

        ApproveResult result = s.Approve(sub.Id, 1);

        Assert.Equal(SubmissionState.PartlyApproved, result.Submission.State);
        Assert.Equal(EditOutcome.NotChosen, result.Submission.Edits[0].Outcome);
        Assert.Equal(EditOutcome.Applied, result.Submission.Edits[1].Outcome);
        Assert.Equal(Text(1), s.Doc.Cues[0].Text);
        Assert.Equal("Second", s.Doc.Cues[1].Text);
    }

    [Fact]
    public void After_lines_were_added_before_it_an_edit_is_found_again_by_start_time_and_text()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(5, Text(5), "Let's leave."));
        s.Saver.Save(new SaveRequest(Item, 3, s.SubPath, s.Hash, new SrtEdit[] { new SrtAddCue("x", 100, 900, "Before everything") }, null));

        ApproveResult result = s.Approve(sub.Id);

        Assert.Equal(SubmissionState.Approved, result.Submission.State);
        Assert.Equal("Let's leave.", s.Doc.Cues[5].Text);
        Assert.Equal("Before everything", s.Doc.Cues[0].Text);
    }

    [Fact]
    public void An_edit_whose_line_changed_since_is_a_conflict_and_the_rest_is_still_written()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "Hi."), new SrtTextEdit(2, Text(2), "Two lines\nof text."));
        s.Saver.Save(new SaveRequest(Item, 3, s.SubPath, s.Hash, new SrtEdit[] { new SrtTextEdit(1, Text(1), "Hello, admin was here.") }, null));

        ApproveResult result = s.Approve(sub.Id);

        Assert.Equal(SubmissionState.PartlyApproved, result.Submission.State);
        Assert.Equal(EditOutcome.Conflict, result.Submission.Edits[0].Outcome);
        Assert.False(string.IsNullOrEmpty(result.Submission.Edits[0].Message));
        Assert.Equal(EditOutcome.Applied, result.Submission.Edits[1].Outcome);
        Assert.Equal("Hello, admin was here.", s.Doc.Cues[0].Text);
        Assert.Equal("Two lines\nof text.", s.Doc.Cues[1].Text);
    }

    [Fact]
    public void When_no_chosen_edit_can_be_written_nothing_is_written_and_it_stays_pending()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "Hi."), new SrtTextEdit(2, Text(2), "Two."));
        s.Saver.Save(new SaveRequest(Item, 3, s.SubPath, s.Hash, new SrtEdit[] { new SrtTextEdit(1, Text(1), "Changed.") }, null));
        string hash = s.Hash;

        ApproveResult result = s.Approve(sub.Id, 0);

        Assert.Null(result.Save);
        Assert.Equal(hash, s.Hash);
        Assert.Equal(SubmissionState.Pending, result.Submission.State);
        Assert.Equal(EditOutcome.Conflict, result.Submission.Edits[0].Outcome);
        Assert.Equal(EditOutcome.Pending, result.Submission.Edits[1].Outcome);
        Assert.Equal(SubmissionState.Pending, s.Store.Find(sub.Id)!.State);

        ApproveResult second = s.Approve(sub.Id, 1);
        Assert.Equal(SubmissionState.PartlyApproved, second.Submission.State);
        Assert.Equal("Two.", s.Doc.Cues[1].Text);
    }

    [Fact]
    public void An_invalid_choice_is_refused()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "Hi."));

        Assert.Equal(SubmissionError.InvalidChoice, Assert.Throws<SubmissionException>(() => s.Approve(sub.Id, 1)).Error);
        Assert.Equal(SubmissionError.InvalidChoice, Assert.Throws<SubmissionException>(() => s.Approve(sub.Id, 0, 0)).Error);
        Assert.Equal(
            SubmissionError.InvalidChoice,
            Assert.Throws<SubmissionException>(() => s.Service.Approve(new ApproveRequest(sub.Id, s.SubPath, "admin", Array.Empty<int>(), null))).Error);
        Assert.Equal(SubmissionState.Pending, s.Store.Find(sub.Id)!.State);
    }

    [Fact]
    public void A_failed_write_leaves_the_file_and_the_submission_as_they_were()
    {
        using var s = new Setup(new CrashAt(WriteStage.TempWritten));
        string hash = s.Hash;
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "Hi."));

        Assert.Throws<SimulatedCrashException>(() => s.Approve(sub.Id));

        Assert.Equal(hash, s.Hash);
        Assert.Equal(SubmissionState.Pending, s.Store.Find(sub.Id)!.State);
    }

    [Fact]
    public void The_store_skips_unreadable_files_and_malformed_ids()
    {
        using var s = new Setup();
        Submission sub = s.Submit(User, new SrtTextEdit(1, Text(1), "Hi."));
        string folder = Path.Combine(s.Root, "submissions");
        File.WriteAllText(Path.Combine(folder, "0123456789abcdef0123456789abcdef.json"), "{ not json");
        File.Copy(Path.Combine(folder, sub.Id + ".json"), Path.Combine(folder, "fedcba9876543210fedcba9876543210.json"));

        Assert.Equal(sub.Id, Assert.Single(s.Store.All()).Id);
        Assert.Null(s.Store.Find("0123456789abcdef0123456789abcdef"));
        Assert.Null(s.Store.Find("../" + sub.Id));
        Assert.Null(s.Store.Find(sub.Id.ToUpperInvariant()));
        Assert.True(SubmissionStore.IsValidId(SubmissionStore.NewId()));
        Assert.Empty(new SubmissionStore(Path.Combine(s.Root, "nothing-here")).All());
    }

    [Fact]
    public void Only_the_newest_decided_submissions_are_kept_and_pending_ones_never_go()
    {
        using var s = new Setup();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var decided = new List<string>();
        for (int i = 0; i < SubmissionService.DecidedKept + 2; i++)
        {
            string id = SubmissionStore.NewId();
            decided.Add(id);
            s.Store.Save(Stored(id, start.AddMinutes(i), SubmissionState.Rejected, start.AddHours(1).AddMinutes(i)));
        }

        string pending = SubmissionStore.NewId();
        s.Store.Save(Stored(pending, start.AddYears(-1), SubmissionState.Pending, null));

        Assert.Equal(2, s.Store.PruneDecided(SubmissionService.DecidedKept));

        List<string> left = s.Store.All().Select(x => x.Id).ToList();
        Assert.Equal(SubmissionService.DecidedKept + 1, left.Count);
        Assert.Contains(pending, left);
        Assert.DoesNotContain(decided[0], left);
        Assert.DoesNotContain(decided[1], left);
        Assert.Contains(decided[2], left);
    }

    private static Submission Stored(string id, DateTimeOffset created, SubmissionState state, DateTimeOffset? decidedAt) => new()
    {
        Id = id,
        UserId = User,
        UserName = "u",
        ItemId = Item,
        StreamIndex = 3,
        BaseHash = new string('a', 64),
        CreatedUtc = created,
        State = state,
        DecidedUtc = decidedAt,
    };
}
