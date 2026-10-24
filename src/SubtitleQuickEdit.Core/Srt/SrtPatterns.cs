using System.Text.RegularExpressions;

namespace SubtitleQuickEdit.Core.Srt;

/// <summary>
/// Regular expressions for lenient .srt parsing.
/// </summary>
internal static partial class SrtPatterns
{
    /// <summary>
    /// Matches a cue time line such as <c>00:00:02,000 --> 00:00:05,000</c>, with lenient spacing, a
    /// period instead of a comma, and optional trailing coordinates.
    /// </summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(
        @"^\s*(\d{1,3}):(\d{1,2}):(\d{1,2})[,.](\d{1,3})\s*-->\s*(\d{1,3}):(\d{1,2}):(\d{1,2})[,.](\d{1,3})(.*)$",
        RegexOptions.CultureInvariant)]
    internal static partial Regex TimeLine();
}
