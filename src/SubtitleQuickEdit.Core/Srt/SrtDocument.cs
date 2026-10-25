using System;
using System.Collections.Generic;
using System.Globalization;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// A parsed .srt file that remembers where every cue sits in the original bytes, so edits can be
/// spliced in without touching any other byte.
/// </summary>
public sealed class SrtDocument : ISubtitleDocument
{
    /// <summary>
    /// The largest time an .srt cue can hold with two-digit hours (99:59:59,999).
    /// </summary>
    public const long MaxTimeMs = (100L * 3600 * 1000) - 1;

    private readonly byte[] data;
    private readonly int[] byteOffsets;

    private SrtDocument(
        byte[] data,
        SubtitleEncoding encoding,
        string lineEnding,
        IReadOnlyList<SubtitleCue> cues,
        int[] byteOffsets,
        int tailTerminators)
    {
        this.data = data;
        Encoding = encoding;
        LineEnding = lineEnding;
        Cues = cues;
        this.byteOffsets = byteOffsets;
        TailTerminators = tailTerminators;
        Hash = SubtitleHash.Compute(data);
    }

    /// <inheritdoc/>
    public SubtitleFormat Format => SubtitleFormat.Srt;

    /// <inheritdoc/>
    public string Hash { get; }

    /// <inheritdoc/>
    public SubtitleEncoding Encoding { get; }

    /// <summary>
    /// Gets the line ending used for new text: <c>\r\n</c>, <c>\n</c> or <c>\r</c>, whichever the file uses most.
    /// </summary>
    public string LineEnding { get; }

    /// <inheritdoc/>
    public IReadOnlyList<SubtitleCue> Cues { get; }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Content => data;

    /// <summary>
    /// Gets how many line terminators end the file (counting blank lines after the last cue text), 0 or more.
    /// </summary>
    internal int TailTerminators { get; }

    /// <summary>
    /// Gets the original bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Data => data;

    /// <summary>
    /// Parses an .srt file leniently. When the file cannot be parsed reliably the result carries the
    /// reason and no document: the track is then not editable.
    /// </summary>
    /// <param name="content">The raw file bytes.</param>
    /// <returns>The parse result.</returns>
    public static SrtParseResult Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SrtParser.Parse(content);
    }

    /// <summary>
    /// Applies edits and returns the new file content. Nothing is written anywhere; the caller writes the
    /// bytes. Every byte outside the edited text and the added or renumbered lines is unchanged.
    /// </summary>
    /// <param name="edits">The edits.</param>
    /// <param name="runtimeMs">The item's runtime in milliseconds; added cues may not end after it. Null skips the check.</param>
    /// <returns>The new content and bookkeeping for the client.</returns>
    /// <exception cref="SubtitleEditException">The edits are invalid or conflict with the file. Nothing was changed.</exception>
    public SubtitleApplyResult Apply(IReadOnlyList<SubtitleEdit> edits, long? runtimeMs = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return SrtEditor.Apply(this, edits, runtimeMs);
    }

    /// <summary>
    /// Formats a time as <c>hh:mm:ss,mmm</c>.
    /// </summary>
    /// <param name="ms">Milliseconds.</param>
    /// <returns>The formatted time.</returns>
    internal static string FormatTime(long ms)
    {
        long hours = ms / 3_600_000;
        long minutes = (ms / 60_000) % 60;
        long seconds = (ms / 1000) % 60;
        long millis = ms % 1000;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours:D2}:{minutes:D2}:{seconds:D2},{millis:D3}");
    }

    /// <summary>
    /// Converts a character offset in the decoded text to a byte offset in the file (BOM included).
    /// </summary>
    /// <param name="charIndex">The character offset.</param>
    /// <returns>The byte offset.</returns>
    internal int ByteOffset(int charIndex) => Encoding.BomLength + byteOffsets[charIndex];

    /// <summary>
    /// Creates a document; used by the parser.
    /// </summary>
    /// <param name="data">The original bytes.</param>
    /// <param name="encoding">The detected encoding.</param>
    /// <param name="lineEnding">The dominant line ending.</param>
    /// <param name="cues">The parsed cues.</param>
    /// <param name="byteOffsets">Character-to-byte offset table for the decoded text.</param>
    /// <param name="tailTerminators">How many line terminators end the file.</param>
    /// <returns>The document.</returns>
    internal static SrtDocument Create(
        byte[] data,
        SubtitleEncoding encoding,
        string lineEnding,
        IReadOnlyList<SubtitleCue> cues,
        int[] byteOffsets,
        int tailTerminators) =>
        new(data, encoding, lineEnding, cues, byteOffsets, tailTerminators);
}
