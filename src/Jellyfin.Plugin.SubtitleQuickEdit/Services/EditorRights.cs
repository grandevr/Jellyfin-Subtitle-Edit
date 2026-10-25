using System;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SubtitleQuickEdit.Configuration;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Services;

/// <summary>
/// What one caller may do with the editor.
/// </summary>
/// <param name="User">The caller, or null when the token names no known user.</param>
/// <param name="IsAdmin">Whether the caller is an administrator: may do everything, including approving submissions.</param>
/// <param name="CanEdit">Whether the caller may save edits directly (admins, and users allowed in the settings).</param>
/// <param name="CanSubmit">Whether the caller may submit edits for review (other users, when submissions are on).</param>
public sealed record EditorRights(User? User, bool IsAdmin, bool CanEdit, bool CanSubmit)
{
    /// <summary>
    /// Gets a value indicating whether the caller may open the editor at all.
    /// </summary>
    public bool CanRead => CanEdit || CanSubmit;

    /// <summary>
    /// Gets the caller's id as 32 lowercase hex characters, or an empty string when unknown.
    /// </summary>
    public string UserId => User?.Id.ToString("N", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Gets the caller's name, or an empty string when unknown.
    /// </summary>
    public string UserName => User?.Username ?? string.Empty;

    /// <summary>
    /// Works out a caller's rights from the settings.
    /// </summary>
    /// <param name="user">The caller, or null.</param>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    /// <param name="config">The plugin settings.</param>
    /// <returns>The rights.</returns>
    public static EditorRights From(User? user, bool isAdmin, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (isAdmin)
        {
            return new EditorRights(user, true, true, false);
        }

        if (user is null)
        {
            return new EditorRights(null, false, false, false);
        }

        bool canEdit = config.UserEdits switch
        {
            UserEditMode.AllUsers => true,
            UserEditMode.SelectedUsers => Array.Exists(
                config.UserEditUserIds ?? [], id => Guid.TryParse(id, out Guid listed) && listed == user.Id),
            _ => false,
        };
        return new EditorRights(user, false, canEdit, !canEdit && config.AllowUserSubmissions);
    }
}
