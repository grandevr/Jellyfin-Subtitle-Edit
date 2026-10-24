using System.Collections.Generic;
using SubtitleQuickEdit.Core.Storage;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// The outcome of a save.
/// </summary>
/// <param name="Hash">SHA-256 (hex) of the file as saved.</param>
/// <param name="SavedCueIndexes">The 1-based positions, in the new file, of every cue that was edited or added.</param>
/// <param name="AddedCues">For each added cue's client id, its 1-based position in the new file.</param>
/// <param name="Method">How the file was replaced.</param>
/// <param name="Notes">Things worth logging, such as recovered leftovers or a folder fsync that failed.</param>
public sealed record SaveResult(
    string Hash,
    IReadOnlyList<int> SavedCueIndexes,
    IReadOnlyDictionary<string, int> AddedCues,
    WriteMethod Method,
    IReadOnlyList<string> Notes);
