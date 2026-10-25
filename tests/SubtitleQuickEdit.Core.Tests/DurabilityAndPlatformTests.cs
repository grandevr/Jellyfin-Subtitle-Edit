using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

/// <summary>
/// Records every fsync request together with what the folder looked like at that moment, then delegates.
/// </summary>
internal sealed class RecordingDurability : IDurability
{
    private readonly IDurability inner = new LinuxDurability();
    private readonly Func<string> snapshot;
    private int fileCalls;

    public RecordingDurability(Func<string> snapshot)
    {
        this.snapshot = snapshot;
    }

    public List<(string Kind, string Name, string State)> Events { get; } = new();

    public Exception? FailDirectory { get; set; }

    public int? FailFileFromCall { get; set; }

    public void SyncFile(FileStream stream)
    {
        fileCalls++;
        if (FailFileFromCall.HasValue && fileCalls >= FailFileFromCall.Value)
        {
            Events.Add(("file", Path.GetFileName(stream.Name), snapshot()));
            throw new IOException("simulated fsync failure on a file");
        }

        // Recorded after the sync did its work (it flushes the stream's buffer), so the snapshot shows what is on disk.
        inner.SyncFile(stream);
        Events.Add(("file", Path.GetFileName(stream.Name), snapshot()));
    }

    public void SyncDirectory(string folder)
    {
        Events.Add(("dir", folder, snapshot()));
        if (FailDirectory is not null)
        {
            throw FailDirectory;
        }

        inner.SyncDirectory(folder);
    }

    /// <summary>
    /// Describes the target file (old, new or other, told apart by length: the test contents differ in length) and how many
    /// temp and in-place files exist.
    /// </summary>
    public static Func<string> Snapshot(string path, byte[] oldContent, byte[] newContent) => () =>
    {
        List<string> names = Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)).ToList();
        // By length: during the in-place copy the writer holds the target open exclusively, so it cannot be read.
        long length = File.Exists(path) ? new FileInfo(path).Length : -1;
        string target = length == oldContent.Length ? "old" : length == newContent.Length ? "new" : "other";
        int tmp = names.Count(n => SafeFileWriter.IsWriterFile(n) && n.EndsWith(".tmp", StringComparison.Ordinal));
        int inPlace = names.Count(n => SafeFileWriter.IsWriterFile(n) && n.EndsWith(".inplace", StringComparison.Ordinal));
        return $"target={target} tmp={tmp} inplace={inPlace}";
    };
}

public class DurabilityAndPlatformTests
{
    private static readonly byte[] Old = Content.Srt("old");
    private static readonly byte[] New = Content.Srt("new, rather longer content so several chunks are needed");

    [Fact]
    public void The_atomic_path_syncs_the_temp_file_before_the_rename_and_the_folder_after_it()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New));

        WriteOutcome outcome = new SafeFileWriter(durability: durability).Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.AtomicRename, outcome.Method);
        Assert.Equal(2, durability.Events.Count);
        Assert.Equal("file", durability.Events[0].Kind);
        Assert.StartsWith(".", durability.Events[0].Name, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", durability.Events[0].Name, StringComparison.Ordinal);
        Assert.Equal("target=old tmp=1 inplace=0", durability.Events[0].State); // synced before anything is renamed
        Assert.Equal("dir", durability.Events[1].Kind);
        Assert.Equal(dir.Root, durability.Events[1].Name);
        Assert.Equal("target=new tmp=0 inplace=0", durability.Events[1].State); // synced after the rename
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void The_in_place_path_syncs_the_marker_rename_before_touching_the_original_and_the_copy_and_cleanup_after()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New));

        WriteOutcome outcome = new SafeFileWriter(new RefusingMetadata(), durability: durability).Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.InPlaceCopy, outcome.Method);
        Assert.Equal(
            new[]
            {
                "file:.tmp:target=old tmp=1 inplace=0",       // the temp file is synced first
                "dir:-:target=old tmp=0 inplace=1",           // the .inplace marker is durable while the original is still untouched
                "file:movie.en.srt:target=new tmp=0 inplace=1", // the in-place copy is synced before the recovery file goes
                "dir:-:target=new tmp=0 inplace=0",           // and the folder after the recovery file is deleted
            },
            durability.Events.Select(e => e.Kind + ":" + Label(e.Name) + ":" + e.State).ToArray());
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void Resuming_an_interrupted_copy_syncs_the_file_and_then_the_folder()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New));

        new SafeFileWriter(new RefusingMetadata(), durability: durability).RecoverLeftovers(path);

        Assert.Equal(
            new[] { "file:movie.en.srt:target=new tmp=0 inplace=1", "dir:-:target=new tmp=0 inplace=0" },
            durability.Events.Select(e => e.Kind + ":" + Label(e.Name) + ":" + e.State).ToArray());
    }

    [Fact]
    public void Removing_a_recovery_file_of_a_completed_save_syncs_the_folder()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        int chunks = (New.Length + 15) / 16;
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten, chunks), copyChunkBytes: 16)
                .Write(path, New, Content.Hash(Old)));
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New));

        new SafeFileWriter(durability: durability).RecoverLeftovers(path);

        Assert.Equal(new[] { "dir:-:target=new tmp=0 inplace=0" }, durability.Events.Select(e => e.Kind + ":" + Label(e.Name) + ":" + e.State).ToArray());
    }

    [Fact]
    public void A_failed_folder_sync_is_a_note_and_the_save_still_succeeds()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New))
        {
            FailDirectory = new IOException("disk says no"),
        };

        WriteOutcome atomic = new SafeFileWriter(durability: durability).Write(path, New, Content.Hash(Old));

        Assert.Equal(New, File.ReadAllBytes(path));
        string note = Assert.Single(atomic.Notes);
        Assert.Contains("fsync", note, StringComparison.Ordinal);
        Assert.Contains("disk says no", note, StringComparison.Ordinal);

        byte[] third = Content.Srt("third");
        WriteOutcome inPlace = new SafeFileWriter(new RefusingMetadata(), durability: durability).Write(path, third, Content.Hash(New));

        Assert.Equal(third, File.ReadAllBytes(path));
        Assert.Equal(2, inPlace.Notes.Count);
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void A_failed_sync_of_the_temp_file_fails_the_save_before_the_original_is_touched()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New)) { FailFileFromCall = 1 };

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter(durability: durability).Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.Io, ex.Error);
        Assert.Equal(Old, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void A_failed_sync_of_the_in_place_copy_keeps_the_recovery_file_and_the_next_save_cleans_up()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new RecordingDurability(RecordingDurability.Snapshot(path, Old, New)) { FailFileFromCall = 2 };

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter(new RefusingMetadata(), durability: durability).Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.Io, ex.Error);
        Assert.NotNull(ex.LeftoverPath);
        Assert.True(File.Exists(ex.LeftoverPath));
        Assert.Equal(New, File.ReadAllBytes(ex.LeftoverPath));
        IReadOnlyList<string> notes = new SafeFileWriter().RecoverLeftovers(path);
        Assert.Single(notes);
        Assert.Empty(dir.WriterFiles());
        Assert.Equal(New, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_linux_durability_syncs_real_files_and_folders_and_reports_a_missing_folder()
    {
        if (!OperatingSystem.IsLinux())
        {
            Integration.SkippedBecause("needs Linux");
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new LinuxDurability();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            durability.SyncFile(stream);
        }

        durability.SyncDirectory(dir.Root);
        Assert.Throws<IOException>(() => durability.SyncDirectory(Path.Combine(dir.Root, "does-not-exist")));
    }

    [Fact]
    public void Without_folder_sync_support_files_are_still_flushed_and_folder_sync_does_nothing()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var durability = new NoDirectorySyncDurability();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            durability.SyncFile(stream);
        }

        durability.SyncDirectory(Path.Combine(dir.Root, "does-not-exist")); // no exception: nothing is done
    }

    // ---- platform selection ----------------------------------------------------------------------------

    [Fact]
    public void Linux_gets_the_system_call_services_and_no_startup_notice()
    {
        PlatformServices services = PlatformServices.Select(isLinux: true);

        Assert.IsType<UnixFileMetadata>(services.Metadata);
        Assert.IsType<LinuxDurability>(services.Durability);
        Assert.Null(services.StartupNotice);
    }

    [Fact]
    public void Other_platforms_get_the_plain_services_and_a_notice_to_log_once()
    {
        PlatformServices services = PlatformServices.Select(isLinux: false);

        Assert.IsType<NullFileMetadata>(services.Metadata);
        Assert.IsType<NoDirectorySyncDurability>(services.Durability);
        Assert.NotNull(services.StartupNotice);
        Assert.Contains("not Linux", services.StartupNotice, StringComparison.Ordinal);
        Assert.Contains("plain atomic rename", services.StartupNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_null_metadata_reads_nothing_and_matches_anything_without_native_calls()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var metadata = new NullFileMetadata();

        Assert.Null(metadata.Get(path));
        Assert.True(metadata.TryMatch(path, new FileOwnership(1, 2, UnixFileMode.UserRead)));
        Assert.Equal(Old, File.ReadAllBytes(path)); // and it did not touch the file
    }

    [Fact]
    public void A_writer_on_the_non_linux_services_uses_the_plain_rename_even_for_a_foreign_group_file()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        PlatformServices services = PlatformServices.Select(isLinux: false);

        WriteOutcome outcome = new SafeFileWriter(services.Metadata, durability: services.Durability)
            .Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.AtomicRename, outcome.Method);
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(outcome.Notes);
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void The_current_platform_selection_matches_the_operating_system()
    {
        PlatformServices current = PlatformServices.Current;
        PlatformServices expected = PlatformServices.Select(OperatingSystem.IsLinux());

        Assert.Equal(expected.Metadata.GetType(), current.Metadata.GetType());
        Assert.Equal(expected.Durability.GetType(), current.Durability.GetType());
        Assert.Equal(expected.StartupNotice, current.StartupNotice);
    }

    private static string Label(string name) =>
        name.StartsWith('.') ? name[^4..] : Path.GetFileName(name) == name ? name : "-";
}
