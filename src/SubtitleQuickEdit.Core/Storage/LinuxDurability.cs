using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// <see cref="IDurability"/> for Linux: <c>fsync</c> on the file, and on the folder opened read-only.
/// Only created by <see cref="PlatformServices"/> on Linux.
/// </summary>
public sealed partial class LinuxDurability : IDurability
{
    private const int ReadOnly = 0;
    private const int Einval = 22;
    private const int Erofs = 30;
    private const int Enotsup = 95;

    /// <inheritdoc />
    public void SyncFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void SyncDirectory(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int fd = Open(folder, ReadOnly);
        if (fd < 0)
        {
            throw new IOException("Could not open folder '" + folder + "' to sync it (error " + Marshal.GetLastPInvokeError() + ").");
        }

        try
        {
            if (Fsync(fd) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is Einval or Erofs or Enotsup)
                {
                    return; // this file system does not support syncing a folder
                }

                throw new IOException("Could not sync folder '" + folder + "' (error " + error + ").");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
