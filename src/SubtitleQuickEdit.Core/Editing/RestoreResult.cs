using System.Collections.Generic;
using SubtitleQuickEdit.Core.Storage;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// The outcome of a restore.
/// </summary>
/// <param name="Hash">SHA-256 (hex) of the file now.</param>
/// <param name="Restored">False when the file already had the wanted content, so nothing was written.</param>
/// <param name="Method">How the file was replaced, or null when nothing was written.</param>
/// <param name="Notes">Things worth logging, such as recovered leftovers or a folder fsync that failed.</param>
public sealed record RestoreResult(
    string Hash,
    bool Restored,
    WriteMethod? Method,
    IReadOnlyList<string> Notes);
