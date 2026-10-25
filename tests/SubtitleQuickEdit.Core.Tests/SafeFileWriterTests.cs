using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SafeFileWriterTests
{
    private static readonly byte[] Old = Content.Srt("old");
    private static readonly byte[] New = Content.Srt("new, rather longer content so several chunks are needed");

    [Fact]
    public void Matching_ownership_replaces_the_file_atomically_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        string link = Path.Combine(dir.Root, "alias.bin");
        Native.HardLink(path, link);

        WriteOutcome outcome = new SafeFileWriter().Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.AtomicRename, outcome.Method);
        Assert.Equal(Content.Hash(New), outcome.NewHash);
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Equal(Old, File.ReadAllBytes(link)); // the old inode: a new file was renamed over the name
        Assert.Empty(dir.WriterFiles());
        Assert.Equal(new[] { "alias.bin", "movie.en.srt" }, dir.AllFiles());
        Assert.Empty(outcome.Notes);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    public void The_original_mode_is_kept_even_when_the_original_is_read_only(UnixFileMode mode)
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old, mode);

        new SafeFileWriter().Write(path, New, Content.Hash(Old));

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Equal(mode, File.GetUnixFileMode(path));
    }

    [Fact]
    public void An_owner_or_group_that_cannot_be_matched_falls_back_to_copying_in_place()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        string link = Path.Combine(dir.Root, "alias.bin");
        Native.HardLink(path, link);

        WriteOutcome outcome = new SafeFileWriter(new RefusingMetadata()).Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.InPlaceCopy, outcome.Method);
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Equal(New, File.ReadAllBytes(link)); // same inode: the original file itself was rewritten
        Assert.Empty(dir.WriterFiles()); // the temp file is gone once the hash was verified
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public void The_fallback_copy_is_written_in_chunks_and_the_result_is_verified_by_hash()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var chunks = new List<WriteStage>();
        var observer = new ActionAt(WriteStage.InPlaceChunkWritten, () => chunks.Add(WriteStage.InPlaceChunkWritten));

        new SafeFileWriter(new RefusingMetadata(), observer, copyChunkBytes: 16).Write(path, New, Content.Hash(Old));

        Assert.Equal((New.Length + 15) / 16, chunks.Count);
        Assert.True(chunks.Count > 3);
        Assert.Equal(Content.Hash(New), Content.Hash(File.ReadAllBytes(path)));
    }

    [Fact]
    public void A_file_that_changed_since_it_was_loaded_is_a_conflict_and_nothing_is_written()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        byte[] changed = Content.Srt("changed by someone else");
        File.WriteAllBytes(path, changed);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter().Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.Conflict, ex.Error);
        Assert.Equal(Content.Hash(changed), ex.CurrentHash);
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void A_change_made_while_the_temp_file_is_being_verified_is_caught_before_the_rename()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        byte[] intruder = Content.Srt("intruder");
        var observer = new ActionAt(WriteStage.TempVerified, () => File.WriteAllBytes(path, intruder));

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter(observer: observer).Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.Conflict, ex.Error);
        Assert.Equal(intruder, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void A_missing_file_is_an_io_error()
    {
        using var dir = new TempDir();

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter().Write(Path.Combine(dir.Root, "nope.srt"), New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.Io, ex.Error);
    }

    [Fact]
    public void A_folder_the_process_cannot_write_to_gives_a_clear_not_writable_error()
    {
        if (UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("running as root, which can write anywhere");
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        File.SetUnixFileMode(dir.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter().Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.NotWritable, ex.Error);
        Assert.Contains(dir.Root, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not writable", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Old, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_read_only_original_that_needs_the_in_place_fallback_is_not_writable_and_leaves_no_temp_file()
    {
        if (UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("running as root, which ignores file permissions");
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter(new RefusingMetadata()).Write(path, New, Content.Hash(Old)));

        Assert.Equal(SubtitleWriteError.NotWritable, ex.Error);
        Assert.Equal(Old, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    // ---- crash and leftover handling -------------------------------------------------------------------

    [Fact]
    public void A_crash_after_the_temp_file_is_written_leaves_a_hidden_non_subtitle_file_and_the_original_untouched()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);

        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(observer: new CrashAt(WriteStage.TempWritten)).Write(path, New, Content.Hash(Old)));

        Assert.Equal(Old, File.ReadAllBytes(path));
        string leftover = Assert.Single(dir.WriterFiles());
        Assert.StartsWith(".", leftover, StringComparison.Ordinal);
        Assert.Equal(".tmp", Path.GetExtension(leftover));
        Assert.True(SafeFileWriter.IsWriterFile(leftover));
    }

    [Fact]
    public void A_stale_temp_file_is_removed_by_the_next_save_which_then_succeeds()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(observer: new CrashAt(WriteStage.TempVerified)).Write(path, New, Content.Hash(Old)));
        Assert.Single(dir.WriterFiles());

        WriteOutcome outcome = new SafeFileWriter().Write(path, New, Content.Hash(Old));

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
        Assert.Contains(outcome.Notes, n => n.Contains("stale temporary file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_crash_just_before_the_rename_leaves_the_original_untouched_and_recoverable_state()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);

        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(observer: new CrashAt(WriteStage.BeforeRename)).Write(path, New, Content.Hash(Old)));

        Assert.Equal(Old, File.ReadAllBytes(path));
        IReadOnlyList<string> notes = new SafeFileWriter().RecoverLeftovers(path);
        Assert.Single(notes);
        Assert.Empty(dir.WriterFiles());
        Assert.Equal(Old, File.ReadAllBytes(path)); // an unapplied temp file is discarded, never applied
    }

    [Fact]
    public void A_crash_in_the_middle_of_the_in_place_copy_leaves_the_complete_new_content_on_disk()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);

        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten, occurrence: 2), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));

        byte[] partial = File.ReadAllBytes(path);
        Assert.Equal(16, partial.Length);
        Assert.Equal(New[..16], partial);
        string leftover = Assert.Single(dir.WriterFiles());
        Assert.Equal(".inplace", Path.GetExtension(leftover));
        Assert.StartsWith(".", leftover, StringComparison.Ordinal);
        Assert.Equal(New, File.ReadAllBytes(Path.Combine(dir.Root, leftover)));
    }

    [Fact]
    public void An_interrupted_in_place_copy_is_resumed_by_recover_leftovers()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten, occurrence: 2), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));

        IReadOnlyList<string> notes = new SafeFileWriter(new RefusingMetadata()).RecoverLeftovers(path);

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
        Assert.Contains("Restored", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void The_next_save_recovers_first_and_then_reports_a_conflict_for_the_stale_base_hash()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));
        byte[] third = Content.Srt("third");

        SubtitleWriteException stale = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter().Write(path, third, Content.Hash(Old)));
        WriteOutcome fresh = new SafeFileWriter().Write(path, third, Content.Hash(New));

        Assert.Equal(SubtitleWriteError.Conflict, stale.Error);
        Assert.Equal(Content.Hash(New), stale.CurrentHash); // the interrupted save was completed first
        Assert.Equal(third, File.ReadAllBytes(path));
        Assert.Equal(WriteMethod.AtomicRename, fresh.Method);
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void When_every_chunk_was_written_but_the_cleanup_never_ran_recovery_just_removes_the_leftover()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        int chunks = (New.Length + 15) / 16;
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten, occurrence: chunks), copyChunkBytes: 16)
                .Write(path, New, Content.Hash(Old)));
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Single(dir.WriterFiles());

        IReadOnlyList<string> notes = new SafeFileWriter().RecoverLeftovers(path);

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
        Assert.Contains("already completed", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_recovery_file_is_never_applied_and_blocks_further_saves_with_a_clear_error()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));
        string leftover = Path.Combine(dir.Root, Assert.Single(dir.WriterFiles()));
        byte[] damaged = File.ReadAllBytes(leftover);
        damaged[10] ^= 0xFF;
        File.WriteAllBytes(leftover, damaged);
        byte[] targetBefore = File.ReadAllBytes(path);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => new SafeFileWriter().Write(path, Content.Srt("x"), Content.Hash(targetBefore)));

        Assert.Equal(SubtitleWriteError.LeftoverUnrecoverable, ex.Error);
        Assert.Equal(leftover, ex.LeftoverPath);
        Assert.Equal(targetBefore, File.ReadAllBytes(path));
        Assert.Equal(damaged, File.ReadAllBytes(leftover));
    }

    [Fact]
    public void Two_recovery_files_for_one_subtitle_are_not_guessed_at()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(new RefusingMetadata(), new CrashAt(WriteStage.InPlaceChunkWritten), copyChunkBytes: 8)
                .Write(path, New, Content.Hash(Old)));
        string first = Path.Combine(dir.Root, Assert.Single(dir.WriterFiles()));
        byte[] other = Content.Srt("another");
        string second = first.Replace(Content.Hash(New)[..32], Content.Hash(other)[..32], StringComparison.Ordinal);
        File.WriteAllBytes(second, other);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(() => new SafeFileWriter().RecoverLeftovers(path));

        Assert.Equal(SubtitleWriteError.LeftoverUnrecoverable, ex.Error);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void Leftovers_of_one_subtitle_are_not_touched_when_another_is_saved()
    {
        using var dir = new TempDir();
        string a = dir.Write("movie.en.srt", Old);
        string b = dir.Write("movie.pl.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(observer: new CrashAt(WriteStage.TempWritten)).Write(a, New, Content.Hash(Old)));
        string leftover = Assert.Single(dir.WriterFiles());

        new SafeFileWriter().Write(b, New, Content.Hash(Old));

        Assert.Equal(new[] { leftover }, dir.WriterFiles());
        Assert.Equal(Old, File.ReadAllBytes(a));
        Assert.Equal(New, File.ReadAllBytes(b));
    }

    [Fact]
    public void Recover_leftovers_with_nothing_to_do_is_a_no_op()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);

        Assert.Empty(new SafeFileWriter().RecoverLeftovers(path));
        Assert.Equal(Old, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("movie.en.srt", false)]
    [InlineData("movie.srt", false)]
    [InlineData(".hidden.srt", false)]
    [InlineData(".movie.en.srt.sqe-0123abcd-0123456789abcdef0123456789abcdef.tmp", true)]
    [InlineData(".movie.en.srt.sqe-0123abcd-0123456789abcdef0123456789abcdef.inplace", true)]
    [InlineData("movie.sqe-x.tmp", false)]
    public void Writer_file_names_are_recognisable_and_never_look_like_subtitles(string name, bool expected)
    {
        Assert.Equal(expected, SafeFileWriter.IsWriterFile(name));
    }

    [Fact]
    public void Temp_names_are_hidden_and_end_in_a_non_subtitle_extension()
    {
        string[] subtitleExtensions = { ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".smi", ".sup", ".mks", ".txt" };
        using var dir = new TempDir();
        string path = dir.Write("TestShow - S01E01.en.srt", Old);
        Assert.Throws<SimulatedCrashException>(
            () => new SafeFileWriter(observer: new CrashAt(WriteStage.TempWritten)).Write(path, New, Content.Hash(Old)));
        string temp = Assert.Single(dir.WriterFiles());

        Assert.StartsWith(".", temp, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetExtension(temp), subtitleExtensions);
        Assert.False(temp.StartsWith("TestShow - S01E01", StringComparison.Ordinal)); // never matches the video's name prefix
    }

    [Fact]
    public void Very_long_file_names_still_get_valid_temp_names()
    {
        using var dir = new TempDir();
        string name = new string('x', 200) + ".en.srt";
        string path = dir.Write(name, Old);

        WriteOutcome outcome = new SafeFileWriter(new RefusingMetadata()).Write(path, New, Content.Hash(Old));

        Assert.Equal(WriteMethod.InPlaceCopy, outcome.Method);
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void Only_one_of_several_simultaneous_saves_from_the_same_base_wins()
    {
        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        string baseHash = Content.Hash(Old);
        byte[][] candidates = Enumerable.Range(0, 8).Select(i => Content.Srt("writer " + i)).ToArray();

        var results = new List<SubtitleWriteError?>();
        Parallel.For(
            0,
            candidates.Length,
            i =>
            {
                try
                {
                    new SafeFileWriter().Write(path, candidates[i], baseHash);
                    lock (results)
                    {
                        results.Add(null);
                    }
                }
                catch (SubtitleWriteException ex)
                {
                    lock (results)
                    {
                        results.Add(ex.Error);
                    }
                }
            });

        Assert.Single(results, r => r is null);
        Assert.Equal(candidates.Length - 1, results.Count(r => r == SubtitleWriteError.Conflict));
        Assert.Contains(candidates, c => c.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void An_edit_applied_to_a_real_subtitle_round_trips_through_the_writer()
    {
        using var dir = new TempDir();
        byte[] original = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish), "\r\n"));
        string path = dir.Write("movie.pl.srt", original);
        SrtDocument doc = SrtDocument.Parse(File.ReadAllBytes(path)).Document!;
        SubtitleApplyResult result = doc.Apply(
            new SubtitleEdit[]
            {
                new SubtitleTextEdit(4, doc.Cues[3].Text, "Nowa, poprawiona kwestia: żółć."),
                new SubtitleAddCue("n", 100, 900, "Na początku"),
            });

        WriteOutcome outcome = new SafeFileWriter(new RefusingMetadata()).Write(path, result.Content, doc.Hash);

        byte[] onDisk = File.ReadAllBytes(path);
        Assert.Equal(result.Hash, outcome.NewHash);
        Assert.Equal(result.Hash, Content.Hash(onDisk));
        SrtDocument reread = SrtDocument.Parse(onDisk).Document!;
        Assert.Equal(13, reread.Cues.Count);
        Assert.Equal("Na początku", reread.Cues[0].Text);
        Assert.Equal("Nowa, poprawiona kwestia: żółć.", reread.Cues[4].Text);
    }

    // ---- real ownership -------------------------------------------------------------------------------

    [Fact]
    public void Real_metadata_reads_the_actual_owner_group_and_mode()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        FileOwnership? ownership = new UnixFileMetadata().Get(path);

        Assert.NotNull(ownership);
        Assert.Equal(UnixFileMetadata.CurrentUid(), (uint?)ownership.Value.Uid);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, ownership.Value.Mode);
    }

    [Fact]
    public void Real_metadata_cannot_hand_a_file_to_a_group_the_process_is_not_in()
    {
        if (!OperatingSystem.IsLinux() || UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("needs Linux and a non-root user");
            return;
        }

        using var dir = new TempDir();
        string path = dir.Write("movie.en.srt", Old);
        var metadata = new UnixFileMetadata();
        FileOwnership own = metadata.Get(path)!.Value;

        Assert.True(metadata.TryMatch(path, own));
        Assert.False(metadata.TryMatch(path, own with { Gid = own.Gid + 12345 }));
    }

    [Fact]
    public void A_real_file_in_a_group_the_process_cannot_join_is_updated_in_place_with_its_group_intact()
    {
        string? prepared = Environment.GetEnvironmentVariable("SQE_MISMATCH_FILE");
        if (string.IsNullOrEmpty(prepared) || !File.Exists(prepared))
        {
            // Needs a file prepared by root: owner = this user, group = one this user is not in.
            Integration.SkippedBecause("SQE_MISMATCH_FILE is not set");
            return;
        }

        var metadata = new UnixFileMetadata();
        File.WriteAllBytes(prepared, Old);
        FileOwnership before = metadata.Get(prepared)!.Value;
        if (UnixFileMetadata.CurrentGid() == before.Gid)
        {
            Integration.SkippedBecause("the fixture's group is the user's own group; run devtools/prepare_mismatch.sh from the dev branch");
            return;
        }

        string link = prepared + ".alias";
        File.Delete(link);
        Native.HardLink(prepared, link);
        try
        {
            WriteOutcome outcome = new SafeFileWriter().Write(prepared, New, Content.Hash(Old));

            Assert.Equal(WriteMethod.InPlaceCopy, outcome.Method);
            Assert.Equal(New, File.ReadAllBytes(prepared));
            Assert.Equal(New, File.ReadAllBytes(link)); // same inode
            FileOwnership after = metadata.Get(prepared)!.Value;
            Assert.Equal(before, after); // owner, group and mode untouched
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(prepared)!).Select(f => Path.GetFileName(f)), SafeFileWriter.IsWriterFile);
        }
        finally
        {
            File.Delete(link);
        }
    }
}
