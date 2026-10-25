namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// The result of deleting an extracted file.
/// </summary>
/// <param name="Record">The record that was removed.</param>
/// <param name="BackupPath">Where the file's last content was kept, or null when the file was already gone.</param>
public sealed record DeleteExtractedResult(ExtractedFile Record, string? BackupPath);
