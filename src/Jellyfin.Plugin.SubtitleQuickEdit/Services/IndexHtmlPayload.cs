namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// What File Transformation hands to the callback: the page as text. It builds this object from JSON, so the property
/// must be public and settable.
/// </summary>
public sealed class IndexHtmlPayload
{
    /// <summary>
    /// Gets or sets the contents of the file being served.
    /// </summary>
    public string Contents { get; set; } = string.Empty;
}
