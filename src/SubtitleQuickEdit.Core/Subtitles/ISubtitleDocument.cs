using System;
using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// A parsed subtitle file that remembers where every cue sits in the original bytes, so edits can be spliced in
/// without touching any other byte. Each format has its own implementation.
/// </summary>
public interface ISubtitleDocument
{
    /// <summary>
    /// Gets the file's format.
    /// </summary>
    SubtitleFormat Format { get; }

    /// <summary>
    /// Gets the SHA-256 of the file content, lower-case hex.
    /// </summary>
    string Hash { get; }

    /// <summary>
    /// Gets the detected encoding.
    /// </summary>
    SubtitleEncoding Encoding { get; }

    /// <summary>
    /// Gets the cues in file order.
    /// </summary>
    IReadOnlyList<SubtitleCue> Cues { get; }

    /// <summary>
    /// Gets the original bytes.
    /// </summary>
    ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// Applies edits and returns the new file content. Nothing is written anywhere; the caller writes the bytes.
    /// Every byte outside the edited text and the added lines is unchanged.
    /// </summary>
    /// <param name="edits">The edits.</param>
    /// <param name="runtimeMs">The item's runtime in milliseconds; added cues may not end after it. Null skips the check.</param>
    /// <returns>The new content and bookkeeping for the client.</returns>
    /// <exception cref="SubtitleEditException">The edits are invalid or conflict with the file. Nothing was changed.</exception>
    SubtitleApplyResult Apply(IReadOnlyList<SubtitleEdit> edits, long? runtimeMs = null);
}
