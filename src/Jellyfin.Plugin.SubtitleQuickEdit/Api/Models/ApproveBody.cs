using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// The body of an approval.
/// </summary>
public class ApproveBody
{
    /// <summary>
    /// Gets or sets the 0-based indexes of the submission's changes to apply, or null to apply all of them.
    /// </summary>
    [JsonPropertyName("edits")]
    public IReadOnlyList<int>? Edits { get; set; }
}
