using System;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Where an extraction would go and what is there now. Nothing is written to make one.
/// </summary>
/// <param name="TargetPath">The file the track would be written to.</param>
/// <param name="Format">The format of the content.</param>
/// <param name="Content">The content that would be written.</param>
/// <param name="ExistingContent">The content of a file already at <paramref name="TargetPath"/>, or null.</param>
/// <param name="ExistingHash">That file's SHA-256 (hex), or null.</param>
/// <param name="ExistingRecord">The newest record of an earlier extraction to that path, or null.</param>
public sealed record ExtractPlan(
    string TargetPath,
    SubtitleFormat Format,
    ReadOnlyMemory<byte> Content,
    ReadOnlyMemory<byte>? ExistingContent,
    string? ExistingHash,
    ExtractedFile? ExistingRecord);
