using System;
using System.IO;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Reads a subtitle file and says whether it can be edited.
/// </summary>
public static class SubtitleTrackInspector
{
    /// <summary>
    /// Reads and parses a subtitle file with the parser for its format.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns>The parsed file, or the reason it is not editable.</returns>
    public static TrackInspection Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        SubtitleFormat? format = SubtitleFormats.FromPath(path);
        if (format is not SubtitleFormat known || !SubtitleFormats.IsEditable(known))
        {
            return new TrackInspection(null, SubtitleFormats.UnsupportedMessage);
        }

        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new TrackInspection(null, "The subtitle file does not exist.");
            }

            if (info.Length > SubtitleLimits.MaxFileBytes)
            {
                return new TrackInspection(null, "The file is too large to edit.");
            }

            data = File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException)
        {
            return new TrackInspection(null, "The Jellyfin process cannot read the subtitle file.");
        }
        catch (IOException ex)
        {
            return new TrackInspection(null, "The subtitle file could not be read: " + ex.Message);
        }

        return Parse(known, data);
    }

    private static TrackInspection Parse(SubtitleFormat format, byte[] data)
    {
        switch (format)
        {
            case SubtitleFormat.Srt:
                SrtParseResult srt = SrtDocument.Parse(data);
                return new TrackInspection(srt.Document, srt.FailureReason);
            default:
                return new TrackInspection(null, SubtitleFormats.UnsupportedMessage);
        }
    }
}
