using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.SubtitleQuickEdit.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit;

/// <summary>
/// The Subtitle Quick Edit plugin: edit the text of external .srt subtitles from the web player.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // A stable folder next to Jellyfin's plugins folder (under the program data folder, "data" in the config folder
        // of the Docker image), outside the versioned plugin folder, so backups and submissions survive plugin
        // updates and rollbacks.
        DataFolder = Path.Combine(applicationPaths.ProgramDataPath, "SubtitleQuickEdit");
    }

    /// <inheritdoc />
    public override string Name => "Subtitle Quick Edit";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b3b6f2a4-6f0e-4d5c-9a0c-5e2f6c1d7a31");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the folder for the plugin's own data (backups, submissions): <c>data/SubtitleQuickEdit</c> under Jellyfin's config folder.
    /// </summary>
    public string DataFolder { get; }

    /// <summary>
    /// Gets the folder for backups of subtitle files from before they were edited.
    /// </summary>
    public string BackupFolder => Path.Combine(DataFolder, "backups");

    /// <summary>
    /// Gets the folder for user submissions waiting for, or decided by, an admin.
    /// </summary>
    public string SubmissionsFolder => Path.Combine(DataFolder, "submissions");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "SubtitleQuickEdit",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
