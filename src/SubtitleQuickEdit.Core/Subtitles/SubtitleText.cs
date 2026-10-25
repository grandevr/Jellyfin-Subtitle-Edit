using System;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Helpers for cue text, which uses <c>\n</c> as its line break in every format.
/// </summary>
public static class SubtitleText
{
    /// <summary>
    /// Turns <c>\r\n</c> and <c>\r</c> into <c>\n</c>.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text with <c>\n</c> breaks.</returns>
    public static string NormalizeBreaks(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }
}
