using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Editing;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Storage;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SubtitleSaveServiceTests
{
    private const string Item = "55de67aa9c161f4100fce9da37b4b0cb";

    private static byte[] Original() =>
        Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish), "\r\n"));

    private static SubtitleSaveService Service(TempDir dir, IFileMetadata? metadata = null, IWriteObserver? observer = null) =>
        new(new SafeFileWriter(metadata, observer), new BackupStore(BackupFolder(dir), BackupMode.OriginalOnly, 3));

    private static string BackupFolder(TempDir dir) => Path.Combine(dir.Root, "backups");

    private static string Subtitle(TempDir dir, byte[] content, string name = "movie.pl.srt")
    {
        Directory.CreateDirectory(Path.Combine(dir.Root, "media"));
        string path = Path.Combine(dir.Root, "media", name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static SaveRequest Request(string path, byte[] baseContent, params SrtEdit[] edits) =>
        new(Item, 4, path, Content.Hash(baseContent), edits, 600_000);

    private static int BackupCount(TempDir dir) =>
        Directory.Exists(BackupFolder(dir)) ? Directory.GetFiles(BackupFolder(dir)).Length : 0;

    [Fact]
    public void A_valid_save_changes_the_file_backs_up_the_original_and_reports_positions_and_hash()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        SrtDocument doc = SrtDocument.Parse(original).Document!;

        SaveResult result = Service(dir).Save(
            Request(path, original, new SrtTextEdit(3, doc.Cues[2].Text, "Nowa kwestia: żółć."), new SrtAddCue("a1", 100, 900, "Na początku")));

        byte[] onDisk = File.ReadAllBytes(path);
        Assert.Equal(result.Hash, Content.Hash(onDisk));
        Assert.Equal(new[] { 1, 4 }, result.SavedCueIndexes);
        Assert.Equal(1, result.AddedCues["a1"]);
        Assert.Equal(WriteMethod.AtomicRename, result.Method);
        SrtDocument after = SrtDocument.Parse(onDisk).Document!;
        Assert.Equal("Na początku", after.Cues[0].Text);
        Assert.Equal("Nowa kwestia: żółć.", after.Cues[3].Text);
        string backup = Assert.Single(Directory.GetFiles(BackupFolder(dir)));
        Assert.EndsWith("_original.srt", backup, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public void The_hash_of_one_save_is_the_base_of_the_next_and_the_original_backup_is_kept()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        SubtitleSaveService service = Service(dir);
        SrtDocument doc = SrtDocument.Parse(original).Document!;
        SaveResult first = service.Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "Pierwsza")));
        byte[] afterFirst = File.ReadAllBytes(path);

        SaveResult second = service.Save(
            new SaveRequest(Item, 4, path, first.Hash, new SrtEdit[] { new SrtTextEdit(2, doc.Cues[1].Text, "Druga") }, null));

        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Directory.GetFiles(BackupFolder(dir)))));
        Assert.NotEqual(afterFirst, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_base_hash_may_be_upper_case_or_padded()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        SrtDocument doc = SrtDocument.Parse(original).Document!;
        var request = new SaveRequest(
            Item, 4, path, "  " + Content.Hash(original).ToUpperInvariant() + " ", new SrtEdit[] { new SrtTextEdit(1, doc.Cues[0].Text, "x") }, null);

        Service(dir).Save(request);

        Assert.Equal("x", SrtDocument.Parse(File.ReadAllBytes(path)).Document!.Cues[0].Text);
    }

    [Fact]
    public void A_stale_base_hash_is_a_conflict_that_carries_the_current_hash_and_writes_nothing()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        byte[] changed = Original().Concat(Fixtures.Windows1250("\r\n")).ToArray();
        File.WriteAllBytes(path, changed);
        SrtDocument doc = SrtDocument.Parse(original).Document!;

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => Service(dir).Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "x"))));

        Assert.Equal(SubtitleWriteError.Conflict, ex.Error);
        Assert.Equal(Content.Hash(changed), ex.CurrentHash);
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Equal(0, BackupCount(dir));
    }

    [Fact]
    public void Old_text_that_no_longer_matches_is_a_conflict_naming_the_cue_and_writes_nothing()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);

        SrtEditException ex = Assert.Throws<SrtEditException>(
            () => Service(dir).Save(Request(path, original, new SrtTextEdit(2, "something else entirely", "x"))));

        Assert.Equal(SrtEditError.TextMismatch, ex.Error);
        Assert.Equal(2, ex.CuePosition);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, BackupCount(dir));
    }

    [Fact]
    public void Invalid_edits_and_an_empty_edit_list_are_refused_without_backups_or_writes()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        SubtitleSaveService service = Service(dir);

        Assert.Equal(SrtEditError.InvalidEdit, Assert.Throws<SrtEditException>(() => service.Save(Request(path, original))).Error);
        Assert.Equal(
            SrtEditError.InvalidEdit,
            Assert.Throws<SrtEditException>(() => service.Save(Request(path, original, new SrtAddCue("a", 900, 100, "x")))).Error);
        Assert.Equal(
            SrtEditError.Unrepresentable,
            Assert.Throws<SrtEditException>(() => service.Save(Request(path, original, new SrtAddCue("a", 100, 900, "Привет")))).Error);
        Assert.Equal(
            SrtEditError.InvalidEdit,
            Assert.Throws<SrtEditException>(() => service.Save(Request(path, original, new SrtAddCue("a", 599_900, 600_100, "x")))).Error); // past the 600 s runtime
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, BackupCount(dir));
    }

    [Fact]
    public void A_subtitle_that_is_not_an_editable_srt_is_refused()
    {
        using var dir = new TempDir();
        string ass = Subtitle(dir, Content.Srt("x"), "movie.en.ass");
        string missing = Path.Combine(dir.Root, "media", "gone.srt");
        string garbage = Subtitle(dir, Fixtures.Utf8("not a subtitle\n"), "garbage.srt");
        SubtitleSaveService service = Service(dir);
        SrtEdit edit = new SrtTextEdit(1, "x", "y");

        foreach (string path in new[] { ass, missing, garbage })
        {
            var ex = Assert.Throws<SubtitleNotEditableException>(
                () => service.Save(new SaveRequest(Item, 1, path, Content.Hash(Content.Srt("x")), new[] { edit }, null)));
            Assert.False(string.IsNullOrWhiteSpace(ex.Reason));
        }

        Assert.Equal(0, BackupCount(dir));
    }

    [Fact]
    public void A_backup_that_cannot_be_written_stops_the_save()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        File.WriteAllBytes(BackupFolder(dir), Array.Empty<byte>()); // a file where the backup folder should be
        SrtDocument doc = SrtDocument.Parse(original).Document!;

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => Service(dir).Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "x"))));

        Assert.Equal(SubtitleWriteError.Io, ex.Error);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_media_folder_the_process_cannot_write_to_is_a_clear_error_and_the_file_is_unchanged()
    {
        if (UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("running as root, which can write anywhere");
            return;
        }

        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        string media = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        SrtDocument doc = SrtDocument.Parse(original).Document!;
        try
        {
            SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
                () => Service(dir).Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "x"))));

            Assert.Equal(SubtitleWriteError.NotWritable, ex.Error);
            Assert.Contains(media, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(media, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_in_place_fallback_is_reported_in_the_result()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        SrtDocument doc = SrtDocument.Parse(original).Document!;

        SaveResult result = Service(dir, new RefusingMetadata()).Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "x")));

        Assert.Equal(WriteMethod.InPlaceCopy, result.Method);
        Assert.Equal(result.Hash, Content.Hash(File.ReadAllBytes(path)));
    }

    [Fact]
    public void A_change_made_while_saving_is_still_caught_by_the_writer()
    {
        using var dir = new TempDir();
        byte[] original = Original();
        string path = Subtitle(dir, original);
        byte[] intruder = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Czech)));
        var observer = new ActionAt(WriteStage.TempVerified, () => File.WriteAllBytes(path, intruder));
        SrtDocument doc = SrtDocument.Parse(original).Document!;

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(
            () => Service(dir, observer: observer).Save(Request(path, original, new SrtTextEdit(1, doc.Cues[0].Text, "x"))));

        Assert.Equal(SubtitleWriteError.Conflict, ex.Error);
        Assert.Equal(intruder, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)), SafeFileWriter.IsWriterFile);
    }

    [Fact]
    public void Constructing_without_a_writer_or_backup_store_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SubtitleSaveService(null!, new BackupStore("/x", BackupMode.OriginalOnly, 1)));
        Assert.Throws<ArgumentNullException>(() => new SubtitleSaveService(new SafeFileWriter(), null!));
    }
}
