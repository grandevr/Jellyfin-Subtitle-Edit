using System.Text.RegularExpressions;

namespace SubtitleQuickEdit.Core.Ass;

/// <summary>
/// Regular expressions for ASS/SSA files.
/// </summary>
internal static partial class AssPatterns
{
    /// <summary>
    /// A time: <c>h:mm:ss.cc</c>. Groups: hours, minutes, seconds, fraction (centiseconds as written).
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^[ \t]*(\d+):(\d{1,2}):(\d{1,2})[.:](\d{1,3})[ \t]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex Time();

    /// <summary>
    /// A section header such as <c>[Events]</c>. Group 1 is the name.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^[ \t]*\[([^\]]+)\][ \t]*$", RegexOptions.CultureInvariant)]
    internal static partial Regex Section();

    /// <summary>
    /// An override block that switches on drawing mode (<c>\p1</c> and up).
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"\{[^}]*\\p[1-9]", RegexOptions.CultureInvariant)]
    internal static partial Regex Drawing();
}
