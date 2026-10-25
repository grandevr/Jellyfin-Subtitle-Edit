namespace SubtitleQuickEdit.Core.Subtitles;

/// <summary>
/// Replaces <paramref name="Length"/> bytes at <paramref name="Offset"/> with <paramref name="Bytes"/>.
/// </summary>
/// <param name="Offset">Where the patch starts in the original bytes.</param>
/// <param name="Length">How many original bytes it replaces (0 inserts).</param>
/// <param name="Bytes">The new bytes.</param>
/// <param name="Order">Tie-break for patches at the same offset: lower goes first.</param>
internal readonly record struct BytePatch(int Offset, int Length, byte[] Bytes, int Order);
