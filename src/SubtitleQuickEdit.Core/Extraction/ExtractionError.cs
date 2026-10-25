namespace SubtitleQuickEdit.Core.Extraction;

/// <summary>
/// Why an extraction was refused. The plugin maps these to HTTP status codes.
/// </summary>
public enum ExtractionError
{
    /// <summary>The track's content is not SubRip, WebVTT or ASS/SSA. HTTP 400.</summary>
    Unsupported,

    /// <summary>The track is larger than the editor handles. HTTP 413.</summary>
    TooLarge,

    /// <summary>The track's content does not parse. HTTP 422.</summary>
    NotParsable,

    /// <summary>A file of the target name exists and replacing it was not asked for. HTTP 409.</summary>
    FileExists,

    /// <summary>Only admins may replace an existing file. HTTP 403.</summary>
    ReplaceNotAllowed,

    /// <summary>The file to be replaced changed since its hash was read. HTTP 409.</summary>
    FileChanged,

    /// <summary>No such extracted file. HTTP 404.</summary>
    NotFound,

    /// <summary>The plugin's data folder could not be read or written. HTTP 500.</summary>
    StorageFailed,
}
