using System;
using System.Collections.Generic;
using System.Globalization;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Vtt;

/// <summary>
/// A parsed WebVTT file that remembers where every cue sits in the original bytes. The header, NOTE, STYLE and REGION
/// blocks, cue identifiers and cue settings are never changed; edits replace cue text and insert new cues.
/// </summary>
public sealed class VttDocument : ISubtitleDocument
{
    /// <summary>
    /// The largest time an added cue may have (99:59:59.999), the same limit the editor uses for .srt.
    /// </summary>
    public const long MaxTimeMs = (100L * 3600 * 1000) - 1;

    private readonly byte[] data;
    private readonly int[] byteOffsets;

    private VttDocument(byte[] data, DecodedText decoded, IReadOnlyList<SubtitleCue> cues, int tailTerminators)
    {
        this.data = data;
        Encoding = decoded.Encoding;
        LineEnding = decoded.LineEnding;
        Cues = cues;
        byteOffsets = decoded.ByteOffsets;
        TailTerminators = tailTerminators;
        Hash = SubtitleHash.Compute(data);
    }

    /// <inheritdoc/>
    public SubtitleFormat Format => SubtitleFormat.WebVtt;

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
    /// Gets how many line terminators end the file (counting blank lines after the last block), 0 or more.
    /// </summary>
    internal int TailTerminators { get; }

    /// <summary>
    /// Gets the original bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Data => data;

    /// <summary>
    /// Parses a WebVTT file leniently. When the file cannot be parsed reliably the result carries the reason and no
    /// document: the track is then not editable.
    /// </summary>
    /// <param name="content">The raw file bytes.</param>
    /// <returns>The parse result.</returns>
    public static VttParseResult Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return VttParser.Parse(content);
    }

    /// <inheritdoc/>
    public SubtitleApplyResult Apply(IReadOnlyList<SubtitleEdit> edits, long? runtimeMs = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return VttEditor.Apply(this, edits, runtimeMs);
    }

    /// <summary>
    /// Formats a time as <c>hh:mm:ss.mmm</c>.
    /// </summary>
    /// <param name="ms">Milliseconds.</param>
    /// <returns>The formatted time.</returns>
    internal static string FormatTime(long ms)
    {
        long hours = ms / 3_600_000;
        long minutes = (ms / 60_000) % 60;
        long seconds = (ms / 1000) % 60;
        long millis = ms % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:D2}:{minutes:D2}:{seconds:D2}.{millis:D3}");
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
    /// <param name="decoded">The decoded text.</param>
    /// <param name="cues">The parsed cues.</param>
    /// <param name="tailTerminators">How many line terminators end the file.</param>
    /// <returns>The document.</returns>
    internal static VttDocument Create(byte[] data, DecodedText decoded, IReadOnlyList<SubtitleCue> cues, int tailTerminators) =>
        new(data, decoded, cues, tailTerminators);
}
