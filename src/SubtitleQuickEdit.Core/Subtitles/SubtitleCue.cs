namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// One cue of a parsed subtitle file.
/// </summary>
public sealed class SubtitleCue
{
    /// <summary>
    /// Gets the 1-based position of the cue in the file. This is what edits refer to.
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Gets the number written on the cue's number line, or null when the line is missing or unusable.
    /// Malformed files can have duplicate, missing or out-of-order numbers.
    /// </summary>
    public int? Number { get; init; }

    /// <summary>
    /// Gets the start time in milliseconds.
    /// </summary>
    public long StartMs { get; init; }

    /// <summary>
    /// Gets the end time in milliseconds.
    /// </summary>
    public long EndMs { get; init; }

    /// <summary>
    /// Gets the cue text with <c>\n</c> as the line break.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the cue's style name (ASS/SSA), or null when the format has none.
    /// </summary>
    public string? Style { get; init; }

    /// <summary>
    /// Gets a value indicating whether the cue's text cannot be edited (an ASS vector drawing).
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Gets where the cue sits in the decoded text. Internal: used to splice bytes.
    /// </summary>
    internal CueLayout Layout { get; init; }
}
