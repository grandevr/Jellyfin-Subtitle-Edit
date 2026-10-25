using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// The result of an extraction.
/// </summary>
/// <param name="Record">The stored record.</param>
/// <param name="BackupPath">The backup of the replaced file, or null when nothing was replaced.</param>
/// <param name="Notes">Things that happened on the way. Fit for the log.</param>
public sealed record ExtractResult(ExtractedFile Record, string? BackupPath, IReadOnlyList<string> Notes);
