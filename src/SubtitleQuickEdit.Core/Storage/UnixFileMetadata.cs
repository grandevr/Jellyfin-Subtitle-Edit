using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// <see cref="IFileMetadata"/> for Linux, using <c>statx</c> (whose struct layout is the same on every
/// architecture) and <c>chown</c>.
/// </summary>
public sealed partial class UnixFileMetadata : IFileMetadata
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxUid = 0x8;
    private const uint StatxGid = 0x10;
    private const int StatxBufferSize = 256;
    private const int PermissionMask = 0x1FF;

    /// <summary>
    /// Gets the user id of the current process, or null when not on Linux.
    /// </summary>
    /// <returns>The uid.</returns>
    public static uint? CurrentUid() => OperatingSystem.IsLinux() ? GetUid() : null;

    /// <summary>
    /// Gets the group id of the current process, or null when not on Linux.
    /// </summary>
    /// <returns>The gid.</returns>
    public static uint? CurrentGid() => OperatingSystem.IsLinux() ? GetGid() : null;

    /// <inheritdoc />
    public unsafe FileOwnership? Get(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        byte* buffer = stackalloc byte[StatxBufferSize];
        int rc = Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid | StatxGid, buffer);
        if (rc != 0)
        {
            return null;
        }

        uint mask = *(uint*)buffer;
        const uint Needed = StatxMode | StatxUid | StatxGid;
        if ((mask & Needed) != Needed)
        {
            return null;
        }

        uint uid = *(uint*)(buffer + 20);
        uint gid = *(uint*)(buffer + 24);
        ushort mode = *(ushort*)(buffer + 28);
        return new FileOwnership(uid, gid, (UnixFileMode)(mode & PermissionMask));
    }

    /// <inheritdoc />
    public bool TryMatch(string path, FileOwnership target)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        File.SetUnixFileMode(path, target.Mode);
        FileOwnership? current = Get(path);
        if (current is null)
        {
            return false;
        }

        if (current.Value.Uid != target.Uid || current.Value.Gid != target.Gid)
        {
            if (Chown(path, target.Uid, target.Gid) != 0)
            {
                return false;
            }

            current = Get(path);
        }

        return current is not null
            && current.Value.Uid == target.Uid
            && current.Value.Gid == target.Gid
            && current.Value.Mode == target.Mode;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int Statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Chown(string path, uint owner, uint group);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "getgid")]
    private static partial uint GetGid();
}
