using System;

namespace SubtitleQuickEdit.Core.Storage;

/// <summary>
/// The platform-specific parts of writing a file: ownership and durability. On Linux they use system calls;
/// elsewhere the Linux classes are never created, so no native code is loaded or run.
/// </summary>
public sealed class PlatformServices
{
    private static readonly Lazy<PlatformServices> CurrentServices = new(() => Select(OperatingSystem.IsLinux()));

    private PlatformServices(IFileMetadata metadata, IDurability durability, string? startupNotice)
    {
        Metadata = metadata;
        Durability = durability;
        StartupNotice = startupNotice;
    }

    /// <summary>
    /// Gets the services for the platform the process is running on.
    /// </summary>
    public static PlatformServices Current => CurrentServices.Value;

    /// <summary>
    /// Gets the ownership reader and matcher.
    /// </summary>
    public IFileMetadata Metadata { get; }

    /// <summary>
    /// Gets the fsync implementation.
    /// </summary>
    public IDurability Durability { get; }

    /// <summary>
    /// Gets a message to log once at startup when the platform is not fully supported, or null.
    /// </summary>
    public string? StartupNotice { get; }

    /// <summary>
    /// Chooses the services for a platform.
    /// </summary>
    /// <param name="isLinux">True for Linux.</param>
    /// <returns>The services.</returns>
    public static PlatformServices Select(bool isLinux) =>
        isLinux
            ? new PlatformServices(new UnixFileMetadata(), new LinuxDurability(), null)
            : new PlatformServices(
                new NullFileMetadata(),
                new NoDirectorySyncDurability(),
                "This platform is not Linux: saved subtitle files will not have their owner and group matched and the folder "
                    + "will not be fsynced; saves use a plain atomic rename.");
}
