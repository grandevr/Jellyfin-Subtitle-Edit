using System;
using System.Collections.Generic;
using System.Text;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// A subtitle file decoded to text, with a table from every character back to its byte offset and the file split into
/// lines. Parsers of every format start here, so edits can be spliced into the original bytes.
/// </summary>
internal sealed class DecodedText
{
    private DecodedText(SubtitleEncoding encoding, string text, int[] byteOffsets, IReadOnlyList<TextLine> lines, string lineEnding)
    {
        Encoding = encoding;
        Text = text;
        ByteOffsets = byteOffsets;
        Lines = lines;
        LineEnding = lineEnding;
    }

    /// <summary>
    /// Gets the detected encoding.
    /// </summary>
    internal SubtitleEncoding Encoding { get; }

    /// <summary>
    /// Gets the decoded text, after the BOM.
    /// </summary>
    internal string Text { get; }

    /// <summary>
    /// Gets, for each character offset in <see cref="Text"/> (and one past the end), its byte offset after the BOM.
    /// </summary>
    internal int[] ByteOffsets { get; }

    /// <summary>
    /// Gets the lines of the text, in order.
    /// </summary>
    internal IReadOnlyList<TextLine> Lines { get; }

    /// <summary>
    /// Gets the line ending used for new text: <c>\r\n</c>, <c>\n</c> or <c>\r</c>, whichever the file uses most.
    /// </summary>
    internal string LineEnding { get; }

    /// <summary>
    /// Decodes a file. The file must survive a decode and re-encode byte for byte, so that splicing is safe.
    /// </summary>
    /// <param name="data">The file content.</param>
    /// <param name="failure">Why it cannot be used, when the result is null.</param>
    /// <returns>The decoded text, or null.</returns>
    internal static DecodedText? Decode(byte[] data, out string? failure)
    {
        failure = null;
        if (data.Length == 0)
        {
            failure = "The file is empty.";
            return null;
        }

        if (data.Length > SubtitleLimits.MaxFileBytes)
        {
            failure = "The file is too large to edit.";
            return null;
        }

        SubtitleEncoding? encoding = SubtitleEncoding.Detect(data);
        if (encoding is null)
        {
            failure = "The text encoding could not be determined or is not supported.";
            return null;
        }

        ReadOnlySpan<byte> body = data.AsSpan(encoding.BomLength);
        string text;
        try
        {
            text = encoding.Encoding.GetString(body);
            byte[] again = encoding.Encoding.GetBytes(text);
            if (!again.AsSpan().SequenceEqual(body))
            {
                failure = "The file does not survive a decode and re-encode in its detected encoding (" + encoding.Name + ").";
                return null;
            }
        }
        catch (DecoderFallbackException)
        {
            failure = "The file contains bytes that are not valid in its detected encoding (" + encoding.Name + ").";
            return null;
        }
        catch (EncoderFallbackException)
        {
            failure = "The file does not survive a decode and re-encode in its detected encoding (" + encoding.Name + ").";
            return null;
        }

        int[] byteOffsets = BuildByteOffsets(text, encoding.Encoding);
        if (byteOffsets[text.Length] != body.Length)
        {
            failure = "The file's byte layout could not be mapped reliably.";
            return null;
        }

        List<TextLine> lines = ScanLines(text, out string lineEnding);
        return new DecodedText(encoding, text, byteOffsets, lines, lineEnding);
    }

    /// <summary>
    /// Gets a line's text, without its terminator.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <returns>The line's characters.</returns>
    internal ReadOnlySpan<char> Of(TextLine line) => Text.AsSpan(line.Start, line.End - line.Start);

    /// <summary>
    /// Counts the line terminators that end the file from a given line on: the one after that line's text and those of
    /// any blank lines after it.
    /// </summary>
    /// <param name="lastContentLine">The index of the last line with content.</param>
    /// <returns>The count, 0 or more.</returns>
    internal int TailTerminators(int lastContentLine)
    {
        int tail = 0;
        for (int i = lastContentLine; i < Lines.Count; i++)
        {
            if (Lines[i].TermEnd > Lines[i].End)
            {
                tail++;
            }
        }

        return tail;
    }

    private static List<TextLine> ScanLines(string text, out string lineEnding)
    {
        var lines = new List<TextLine>();
        int crlf = 0;
        int lf = 0;
        int cr = 0;
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\r' && text[i] != '\n')
            {
                i++;
            }

            int end = i;
            if (i < text.Length)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i += 2;
                    crlf++;
                }
                else if (text[i] == '\r')
                {
                    i++;
                    cr++;
                }
                else
                {
                    i++;
                    lf++;
                }
            }

            lines.Add(new TextLine(start, end, i));
        }

        if (crlf > lf && crlf >= cr)
        {
            lineEnding = "\r\n";
        }
        else if (cr > lf && cr > crlf)
        {
            lineEnding = "\r";
        }
        else
        {
            lineEnding = "\n";
        }

        return lines;
    }

    private static int[] BuildByteOffsets(string text, Encoding encoding)
    {
        bool utf8 = encoding is UTF8Encoding;
        bool utf16 = encoding is UnicodeEncoding;
        bool singleByte = encoding.IsSingleByte;
        var map = new int[text.Length + 1];
        int position = 0;
        int i = 0;
        while (i < text.Length)
        {
            map[i] = position;
            int chars = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            int bytes;
            if (utf16)
            {
                bytes = 2 * chars;
            }
            else if (singleByte)
            {
                bytes = 1;
            }
            else if (utf8)
            {
                char c = text[i];
                bytes = chars == 2 ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            }
            else
            {
                bytes = encoding.GetByteCount(text.AsSpan(i, chars));
            }

            if (chars == 2)
            {
                map[i + 1] = position;
            }

            position += bytes;
            i += chars;
        }

        map[text.Length] = position;
        return map;
    }
}
