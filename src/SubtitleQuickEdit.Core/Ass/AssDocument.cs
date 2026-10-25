using System;
using System.Collections.Generic;
using System.Globalization;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// A parsed ASS or SSA file. Its cues are the Dialogue lines of the [Events] section, in file order; only their Text
/// field is ever changed. Styles, fonts, Comment lines, the other fields and override tags stay as they are. In cue
/// text, the file's <c>\N</c> hard line break is a <c>\n</c>.
/// </summary>
public sealed class AssDocument : ISubtitleDocument
{
    /// <summary>
    /// The largest time an added line may have (99:59:59.99).
    /// </summary>
    public const long MaxTimeMs = (100L * 3600 * 1000) - 10;

    private readonly byte[] data;
    private readonly int[] byteOffsets;

    private AssDocument(
        byte[] data,
        DecodedText decoded,
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<AssEvent> events,
        int startField,
        int endField)
    {
        this.data = data;
        Encoding = decoded.Encoding;
        LineEnding = decoded.LineEnding;
        Cues = cues;
        Events = events;
        StartField = startField;
        EndField = endField;
        byteOffsets = decoded.ByteOffsets;
        Hash = SubtitleHash.Compute(data);
    }

    /// <inheritdoc/>
    public SubtitleFormat Format => SubtitleFormat.Ass;

    /// <inheritdoc/>
    public string Hash { get; }

    /// <inheritdoc/>
    public SubtitleEncoding Encoding { get; }

    /// <summary>
    /// Gets the line ending used for new lines: <c>\r\n</c>, <c>\n</c> or <c>\r</c>, whichever the file uses most.
    /// </summary>
    public string LineEnding { get; }

    /// <inheritdoc/>
    public IReadOnlyList<SubtitleCue> Cues { get; }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Content => data;

    /// <summary>
    /// Gets each cue's Dialogue line, in the same order as <see cref="Cues"/>.
    /// </summary>
    internal IReadOnlyList<AssEvent> Events { get; }

    /// <summary>
    /// Gets the index of the Start field among the fields before Text.
    /// </summary>
    internal int StartField { get; }

    /// <summary>
    /// Gets the index of the End field among the fields before Text.
    /// </summary>
    internal int EndField { get; }

    /// <summary>
    /// Gets the original bytes.
    /// </summary>
    internal ReadOnlySpan<byte> Data => data;

    /// <summary>
    /// Parses an ASS or SSA file. When the file cannot be parsed reliably the result carries the reason and no
    /// document: the track is then not editable.
    /// </summary>
    /// <param name="content">The raw file bytes.</param>
    /// <returns>The parse result.</returns>
    public static AssParseResult Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return AssParser.Parse(content);
    }

    /// <inheritdoc/>
    public SubtitleApplyResult Apply(IReadOnlyList<SubtitleEdit> edits, long? runtimeMs = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return AssEditor.Apply(this, edits, runtimeMs);
    }

    /// <summary>
    /// Rounds milliseconds to the centiseconds an ASS time holds.
    /// </summary>
    /// <param name="ms">Milliseconds.</param>
    /// <returns>Milliseconds, a multiple of 10.</returns>
    internal static long RoundTime(long ms) => (ms + 5) / 10 * 10;

    /// <summary>
    /// Formats a time as <c>h:mm:ss.cc</c>.
    /// </summary>
    /// <param name="ms">Milliseconds, a multiple of 10.</param>
    /// <returns>The formatted time.</returns>
    internal static string FormatTime(long ms)
    {
        long hours = ms / 3_600_000;
        long minutes = (ms / 60_000) % 60;
        long seconds = (ms / 1000) % 60;
        long centis = (ms % 1000) / 10;
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:D2}:{seconds:D2}.{centis:D2}");
    }

    /// <summary>
    /// Turns cue text into the Text field form: <c>\n</c> becomes <c>\N</c>.
    /// </summary>
    /// <param name="text">The cue text.</param>
    /// <returns>The field text.</returns>
    internal static string ToField(string text) => text.Replace("\n", "\\N", StringComparison.Ordinal);

    /// <summary>
    /// Turns a Text field into cue text: <c>\N</c> becomes <c>\n</c>.
    /// </summary>
    /// <param name="field">The field text.</param>
    /// <returns>The cue text.</returns>
    internal static string FromField(string field) => field.Replace("\\N", "\n", StringComparison.Ordinal);

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
    /// <param name="events">Each cue's Dialogue line.</param>
    /// <param name="startField">The index of the Start field.</param>
    /// <param name="endField">The index of the End field.</param>
    /// <returns>The document.</returns>
    internal static AssDocument Create(
        byte[] data,
        DecodedText decoded,
        IReadOnlyList<SubtitleCue> cues,
        IReadOnlyList<AssEvent> events,
        int startField,
        int endField) =>
        new(data, decoded, cues, events, startField, endField);
}
