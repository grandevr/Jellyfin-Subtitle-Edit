using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Builds a file's new content from its original bytes and a set of patches, so every byte outside the patches stays
/// as it was.
/// </summary>
internal static class ByteSplicer
{
    /// <summary>
    /// Encodes text in the file's encoding, turning <c>\n</c> into the file's line ending first.
    /// </summary>
    /// <param name="encoding">The file's encoding.</param>
    /// <param name="text">The text, with <c>\n</c> breaks.</param>
    /// <param name="eol">The line ending to use, or null to leave breaks as they are.</param>
    /// <param name="cuePosition">The cue the text belongs to, for messages.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="SubtitleEditException">A character cannot be represented in the encoding.</exception>
    internal static byte[] Encode(SubtitleEncoding encoding, string text, string? eol, int? cuePosition)
    {
        string withBreaks = eol is null ? text : text.Replace("\n", eol, StringComparison.Ordinal);
        try
        {
            return encoding.Encoding.GetBytes(withBreaks);
        }
        catch (EncoderFallbackException ex)
        {
            string shown = ex.CharUnknownHigh != '\0'
                ? string.Create(CultureInfo.InvariantCulture, $"U+{char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow):X4}")
                : string.Create(CultureInfo.InvariantCulture, $"'{ex.CharUnknown}' (U+{(int)ex.CharUnknown:X4})");
            throw new SubtitleEditException(
                SubtitleEditError.Unrepresentable,
                $"The character {shown} cannot be stored in this file's {encoding.Name} encoding.",
                cuePosition);
        }
    }

    /// <summary>
    /// Applies the patches, in offset order (then by <see cref="BytePatch.Order"/>), to the original bytes.
    /// </summary>
    /// <param name="original">The original bytes.</param>
    /// <param name="patches">The patches; none may overlap.</param>
    /// <returns>The new content.</returns>
    internal static byte[] Splice(ReadOnlySpan<byte> original, IEnumerable<BytePatch> patches)
    {
        List<BytePatch> ordered = patches.OrderBy(p => p.Offset).ThenBy(p => p.Order).ToList();
        using var output = new MemoryStream(original.Length + 256);
        int cursor = 0;
        foreach (BytePatch patch in ordered)
        {
            if (patch.Offset < cursor)
            {
                throw new SubtitleEditException(SubtitleEditError.VerificationFailed, "Internal error: overlapping edits.");
            }

            output.Write(original[cursor..patch.Offset]);
            output.Write(patch.Bytes);
            cursor = patch.Offset + patch.Length;
        }

        output.Write(original[cursor..]);
        return output.ToArray();
    }

    /// <summary>
    /// Repeats a line ending 0, 1 or 2 times.
    /// </summary>
    /// <param name="value">The line ending.</param>
    /// <param name="count">How often.</param>
    /// <returns>The repeated text.</returns>
    internal static string Repeat(string value, int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return count == 1 ? value : value + value;
    }
}
