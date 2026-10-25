using System;
using System.IO;
using SubtitleQuickEdit.Core.Ass;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;
using SubtitleQuickEdit.Core.Vtt;

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

        return InspectContent(known, data);
    }

    /// <summary>
    /// Parses subtitle content that is not (yet) a file, with the parser for <paramref name="format"/>.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <param name="data">The content.</param>
    /// <returns>The parsed content, or the reason it is not editable.</returns>
    public static TrackInspection InspectContent(SubtitleFormat format, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (format)
        {
            case SubtitleFormat.Srt:
                SrtParseResult srt = SrtDocument.Parse(data);
                return new TrackInspection(srt.Document, srt.FailureReason);
            case SubtitleFormat.WebVtt:
                VttParseResult vtt = VttDocument.Parse(data);
                return new TrackInspection(vtt.Document, vtt.FailureReason);
            case SubtitleFormat.Ass:
                AssParseResult ass = AssDocument.Parse(data);
                return new TrackInspection(ass.Document, ass.FailureReason);
            default:
                return new TrackInspection(null, SubtitleFormats.UnsupportedMessage);
        }
    }
}
