using System.Text.RegularExpressions;

namespace SubtitleQuickEdit.Core.Vtt;

/// <summary>
/// Regular expressions for WebVTT files.
/// </summary>
internal static partial class VttPatterns
{
    /// <summary>
    /// A cue timing line: <c>[hh:]mm:ss.ttt --&gt; [hh:]mm:ss.ttt</c>, then optional cue settings. Groups 1 to 4 are
    /// the start's hours (may be empty), minutes, seconds and milliseconds, 5 to 8 the end's.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(
        @"^[ \t]*(?:(\d+):)?(\d{2}):(\d{2})\.(\d{3})[ \t]*-->[ \t]*(?:(\d+):)?(\d{2}):(\d{2})\.(\d{3})(?:[ \t].*)?$",
        RegexOptions.CultureInvariant)]
    internal static partial Regex TimingLine();

    /// <summary>
    /// The signature line: <c>WEBVTT</c> alone or followed by a space or tab and any text.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^WEBVTT(?:[ \t].*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex Signature();

    /// <summary>
    /// The first line of a NOTE, STYLE or REGION block.
    /// </summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex(@"^(?:NOTE|STYLE|REGION)(?:[ \t].*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex OtherBlock();
}
