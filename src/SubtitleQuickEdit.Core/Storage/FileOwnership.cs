using System.IO;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// Owner, group and permission bits of a file.
/// </summary>
/// <param name="Uid">The owning user id.</param>
/// <param name="Gid">The owning group id.</param>
/// <param name="Mode">The permission bits.</param>
public readonly record struct FileOwnership(uint Uid, uint Gid, UnixFileMode Mode);
