using System;
using System.Collections.Generic;

namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// The result of <see cref="ISubtitleDocument.Apply"/>.
/// </summary>
public sealed class SubtitleApplyResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleApplyResult"/> class.
    /// </summary>
    /// <param name="content">The new file content.</param>
    /// <param name="hash">SHA-256 of <paramref name="content"/>, lower-case hex.</param>
    /// <param name="addedCuePositions">For each added cue's client id, its 1-based position (and number) in the new file.</param>
    /// <param name="editedCuePositions">The 1-based positions, in the new file, of the cues whose text was edited.</param>
    public SubtitleApplyResult(
        ReadOnlyMemory<byte> content,
        string hash,
        IReadOnlyDictionary<string, int> addedCuePositions,
        IReadOnlyList<int> editedCuePositions)
    {
        Content = content;
        Hash = hash;
        AddedCuePositions = addedCuePositions;
        EditedCuePositions = editedCuePositions;
    }

    /// <summary>
    /// Gets the new file content.
    /// </summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// Gets the SHA-256 of <see cref="Content"/>, lower-case hex.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    /// Gets, for each added cue's client id, its 1-based position (and number) in the new file.
    /// </summary>
    public IReadOnlyDictionary<string, int> AddedCuePositions { get; }

    /// <summary>
    /// Gets the 1-based positions, in the new file, of the cues whose text was edited.
    /// </summary>
    public IReadOnlyList<int> EditedCuePositions { get; }
}
