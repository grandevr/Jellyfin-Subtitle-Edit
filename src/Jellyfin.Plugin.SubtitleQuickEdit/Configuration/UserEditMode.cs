namespace Jellyfin.Plugin.SubtitleQuickEdit.Configuration;

/// <summary>
/// Which non-admin users may save edits directly, without an admin's approval.
/// </summary>
public enum UserEditMode
{
    /// <summary>
    /// Only admins save edits.
    /// </summary>
    Off,

    /// <summary>
    /// Every user may save edits to the items they can see.
    /// </summary>
    AllUsers,

    /// <summary>
    /// The users listed in <see cref="PluginConfiguration.UserEditUserIds"/> may save edits to the items they can see.
    /// </summary>
    SelectedUsers,
}
