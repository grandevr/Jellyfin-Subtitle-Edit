using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;
using SubtitleQuickEdit.Core.Storage;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Configuration;

/// <summary>
/// Plugin settings. Everything site-specific lives here: nothing is hardcoded.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        AllowUserSubmissions = false;
        UserEdits = UserEditMode.Off;
        UserEditUserIds = [];
        MaxPendingSubmissionsPerUser = 50;
        BackupMode = BackupMode.OriginalOnly;
        BackupKeepLastSaves = 5;
    }

    /// <summary>
    /// Gets or sets a value indicating whether non-admin users may submit edits for an admin to approve.
    /// </summary>
    public bool AllowUserSubmissions { get; set; }

    /// <summary>
    /// Gets or sets which non-admin users may save edits directly, without an admin's approval. Such users still
    /// cannot approve submissions or change settings.
    /// </summary>
    public UserEditMode UserEdits { get; set; }

    /// <summary>
    /// Gets or sets, for <see cref="UserEditMode.SelectedUsers"/>, the ids of the users who may save edits directly.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML and JSON plugin settings need a settable array.")]
    public string[] UserEditUserIds { get; set; }

    /// <summary>
    /// Gets or sets how many submissions one user may have waiting for review.
    /// </summary>
    public int MaxPendingSubmissionsPerUser { get; set; }

    /// <summary>
    /// Gets or sets which backups of a subtitle file are kept.
    /// </summary>
    public BackupMode BackupMode { get; set; }

    /// <summary>
    /// Gets or sets, for <see cref="BackupMode.OriginalPlusLastN"/>, how many earlier versions are kept besides the original.
    /// </summary>
    public int BackupKeepLastSaves { get; set; }
}
