using System;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// The File Transformation callback: adds the plugin's script tag and stylesheet link to jellyfin-web's index.html.
/// File Transformation finds this class and method by name (see <see cref="FileTransformationRegistrar"/>), so they
/// must stay public and static.
/// </summary>
public static class IndexHtmlInjector
{
    /// <summary>
    /// The attribute on both injected tags. If it is already in the page, nothing is added.
    /// </summary>
    public const string Marker = "data-subtitle-quick-edit";

    /// <summary>
    /// Adds the script tag and the stylesheet link before the closing body tag.
    /// </summary>
    /// <param name="payload">The page.</param>
    /// <returns>The page with the two tags, or the page unchanged if the tags are already there or it has no body end.</returns>
    public static string Inject(IndexHtmlPayload payload)
    {
        string html = payload?.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        int end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return html;
        }

        // Relative to /web/index.html, so it also works when Jellyfin is served under a base path.
        string version = Uri.EscapeDataString(typeof(IndexHtmlInjector).Assembly.GetName().Version?.ToString() ?? "0");
        string tags =
            "<!-- Subtitle Editor -->\n" +
            "<link rel=\"stylesheet\" href=\"../SubtitleQuickEdit/Client/quickedit.css?v=" + version + "\" " + Marker + ">\n" +
            "<script src=\"../SubtitleQuickEdit/Client/quickedit.js?v=" + version + "\" " + Marker + "></script>\n";
        return html.Insert(end, tags);
    }
}
