using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;

namespace SubtitleQuickEdit.Core.Tests;

/// <summary>
/// A temporary folder for writer tests.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "sqe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Write(string name, byte[] content, UnixFileMode? mode = null)
    {
        string path = Path.Combine(Root, name);
        File.WriteAllBytes(path, content);
        if (mode.HasValue)
        {
            File.SetUnixFileMode(path, mode.Value);
        }

        return path;
    }

    public List<string> WriterFiles() =>
        Directory.GetFiles(Root).Select(f => Path.GetFileName(f)).Where(SafeFileWriter.IsWriterFile).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public List<string> AllFiles() =>
        Directory.GetFiles(Root).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public void Dispose()
    {
        try
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Leave it for the OS temp cleaner.
        }
    }
}

/// <summary>
/// Metadata that reports the real ownership but pretends the group or owner can never be matched, like a file
/// owned by a group the process is not a member of.
/// </summary>
internal sealed class RefusingMetadata : IFileMetadata
{
    private readonly UnixFileMetadata inner = new();

    public FileOwnership? Get(string path) => inner.Get(path);

    public bool TryMatch(string path, FileOwnership target) => false;
}

/// <summary>
/// Thrown by observers to simulate the process dying at a given point.
/// </summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("simulated crash")
    {
    }
}

/// <summary>
/// Throws <see cref="SimulatedCrashException"/> the n-th time a stage is reached.
/// </summary>
internal sealed class CrashAt : IWriteObserver
{
    private readonly WriteStage stage;
    private readonly int occurrence;
    private int seen;

    public CrashAt(WriteStage stage, int occurrence = 1)
    {
        this.stage = stage;
        this.occurrence = occurrence;
    }

    public void OnStage(WriteStage reached)
    {
        if (reached == stage && ++seen == occurrence)
        {
            throw new SimulatedCrashException();
        }
    }
}

/// <summary>
/// Runs an action when a stage is reached.
/// </summary>
internal sealed class ActionAt : IWriteObserver
{
    private readonly WriteStage stage;
    private readonly Action action;

    public ActionAt(WriteStage stage, Action action)
    {
        this.stage = stage;
        this.action = action;
    }

    public void OnStage(WriteStage reached)
    {
        if (reached == stage)
        {
            action();
        }
    }
}

internal static partial class Native
{
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinkNative(string existing, string created);

    public static void HardLink(string existing, string created)
    {
        if (LinkNative(existing, created) != 0)
        {
            throw new IOException("link() failed: " + Marshal.GetLastPInvokeError());
        }
    }
}

/// <summary>
/// Some tests need things only the test server run provides (a file in a foreign group, the test server's
/// subtitle files, a non-root user). Without those they return early; with SQE_REQUIRE_INTEGRATION=1 an early
/// return is a failure, so a green run cannot silently have skipped them.
/// </summary>
internal static class Integration
{
    public static bool Required => Environment.GetEnvironmentVariable("SQE_REQUIRE_INTEGRATION") == "1";

    public static void SkippedBecause(string reason)
    {
        if (Required)
        {
            throw new InvalidOperationException("Integration test could not run: " + reason);
        }
    }
}

internal static class Content
{
    /// <summary>
    /// Some valid .srt content, different for each seed.
    /// </summary>
    public static byte[] Srt(string marker) =>
        Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(new[] { "First " + marker, "Second " + marker, "Third " + marker })));

    public static string Hash(byte[] data) => SrtDocument.ComputeHash(data);
}
