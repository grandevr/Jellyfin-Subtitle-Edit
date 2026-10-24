using System;
using System.IO;
using SubtitleQuickEdit.Core.Srt;

namespace SubtitleQuickEdit.Core.Editing;

/// <summary>
/// Reads a subtitle file and says whether it can be edited.
/// </summary>
public static class SrtTrackInspector
{
    /// <summary>
    /// Reads and parses a subtitle file.
    /// </summary>
    /// <param name="path">The subtitle file.</param>
    /// <returns>The parsed file, or the reason it is not editable.</returns>
    public static TrackInspection Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            return new TrackInspection(null, "Only SubRip (.srt) subtitles can be edited.");
        }

        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new TrackInspection(null, "The subtitle file does not exist.");
            }

            if (info.Length > SrtDocument.MaxFileBytes)
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

        SrtParseResult parsed = SrtDocument.Parse(data);
        return new TrackInspection(parsed.Document, parsed.FailureReason);
    }
}
