using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubtitleQuickEdit.Api.Models;

/// <summary>
/// A subtitle file made from an embedded track, as the review list shows it.
/// </summary>
public class ExtractedFileDto
{
    /// <summary>
    /// Gets or sets the record id.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's name, or null when the item is gone.
    /// </summary>
    [JsonPropertyName("itemName")]
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the index of the embedded stream it was made from.
    /// </summary>
    [JsonPropertyName("sourceStreamIndex")]
    public int SourceStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the file's name.
    /// </summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's subtitle stream index in the library, or null.
    /// </summary>
    [JsonPropertyName("streamIndex")]
    public int? StreamIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file is still there.
    /// </summary>
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file was changed since it was made.
    /// </summary>
    [JsonPropertyName("edited")]
    public bool Edited { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it replaced an earlier file.
    /// </summary>
    [JsonPropertyName("replaced")]
    public bool Replaced { get; set; }

    /// <summary>
    /// Gets or sets the id of the user who made it.
    /// </summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the user who made it.
    /// </summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when it was made.
    /// </summary>
    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; set; }
}
