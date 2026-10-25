using System;
using System.IO;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Maps subtitle files to their format, and says which formats can be edited.
/// </summary>
public static class SubtitleFormats
{
    /// <summary>
    /// The message for a file whose format the editor does not handle.
    /// </summary>
    public const string UnsupportedMessage = "Only SubRip (.srt) subtitles can be edited.";

    /// <summary>
    /// Gets a file's format from its extension.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>The format, or null when the extension is not a known text subtitle format.</returns>
    public static SubtitleFormat? FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Srt;
        }

        if (extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.WebVtt;
        }

        if (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
        {
            return SubtitleFormat.Ass;
        }

        return null;
    }

    /// <summary>
    /// Gets a value indicating whether files of a format can be edited by this build.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>True when the editor can parse and write it.</returns>
    public static bool IsEditable(SubtitleFormat format) => format == SubtitleFormat.Srt;

    /// <summary>
    /// Gets a value indicating whether a file can be edited by this build, judged by its extension.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns>True when its format is editable.</returns>
    public static bool IsEditable(string path) => FromPath(path) is SubtitleFormat format && IsEditable(format);

    /// <summary>
    /// Gets the short name of a format, as the API reports it: <c>srt</c>, <c>vtt</c> or <c>ass</c>.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>The name.</returns>
    public static string Name(SubtitleFormat format) => format switch
    {
        SubtitleFormat.WebVtt => "vtt",
        SubtitleFormat.Ass => "ass",
        _ => "srt",
    };
}
