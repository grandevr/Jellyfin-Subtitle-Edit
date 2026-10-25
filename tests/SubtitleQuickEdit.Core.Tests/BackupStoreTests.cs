using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using SubtitleQuickEdit.Core.Storage;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class BackupStoreTests
{
    private const string Item = "55de67aa9c161f4100fce9da37b4b0cb";

    [Fact]
    public void The_first_save_keeps_the_original_named_by_item_stream_path_and_time()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk", "nested");
        byte[] original = Content.Srt("original");
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);

        var created = store.BeforeSave(Item, 4, "/media/show/movie.pl.srt", original);

        string path = Assert.Single(created);
        Assert.Matches(new Regex("^" + Item + @"_4_[0-9a-f]{8}_\d{8}T\d{9}Z_original\.srt$"), Path.GetFileName(path));
        Assert.Equal(folder, Path.GetDirectoryName(path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("/media/movie.en.vtt", ".vtt")]
    [InlineData("/media/movie.en.ASS", ".ass")]
    [InlineData("/media/movie.en.ssa", ".ssa")]
    public void Backups_keep_the_subtitles_own_extension_and_are_found_by_hash(string subtitle, string extension)
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalPlusLastN, 3);
        byte[] v1 = Content.Srt("v1");
        byte[] v2 = Content.Srt("v2");
        byte[] restore = Content.Srt("r");

        string original = store.BeforeSave(Item, 2, subtitle, v1).Single();
        string save = store.BeforeSave(Item, 2, subtitle, v2).Single();
        string point = store.KeepRestorePoint(Item, 2, subtitle, restore);

        Assert.EndsWith("_original" + extension, original, StringComparison.Ordinal);
        Assert.EndsWith("_save" + extension, save, StringComparison.Ordinal);
        Assert.EndsWith("_restore" + extension, point, StringComparison.Ordinal);
        Assert.Equal(v1, store.FindByHash(Item, 2, subtitle, Content.Hash(v1)));
        Assert.Equal(restore, store.FindByHash(Item, 2, subtitle, Content.Hash(restore)));
    }

    [Fact]
    public void Later_saves_in_original_only_mode_back_up_nothing_and_never_touch_the_original_backup()
    {
        using var dir = new TempDir();
        var store = new BackupStore(Path.Combine(dir.Root, "bk"), BackupMode.OriginalOnly, 3);
        byte[] v1 = Content.Srt("v1");
        string first = store.BeforeSave(Item, 4, "/media/movie.pl.srt", v1).Single();

        var second = store.BeforeSave(Item, 4, "/media/movie.pl.srt", Content.Srt("v2"));
        var third = store.BeforeSave(Item, 4, "/media/movie.pl.srt", Content.Srt("v3"));

        Assert.Empty(second);
        Assert.Empty(third);
        Assert.Equal(new[] { first }, Directory.GetFiles(Path.Combine(dir.Root, "bk")));
        Assert.Equal(v1, File.ReadAllBytes(first));
    }

    [Fact]
    public void Original_plus_last_n_keeps_the_original_and_only_the_newest_n_earlier_versions()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalPlusLastN, 2);
        string first = store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("v1")).Single();
        for (int i = 2; i <= 6; i++)
        {
            Assert.Single(store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("v" + i)));
        }

        string[] files = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        string[] saves = files.Where(f => f.EndsWith("_save.srt", StringComparison.Ordinal)).ToArray();

        Assert.Equal(3, files.Length);
        Assert.Contains(first, files);
        Assert.Equal(Content.Srt("v1"), File.ReadAllBytes(first));
        Assert.Equal(2, saves.Length);
        Assert.Equal(new[] { Content.Srt("v5"), Content.Srt("v6") }, saves.Select(File.ReadAllBytes).ToArray()); // the two most recent
    }

    [Fact]
    public void Saves_in_the_same_millisecond_still_keep_the_newest()
    {
        // A stopped clock puts every save in the same millisecond (none is pruned: 50 are kept); each new name must
        // still sort as the newest.
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var now = new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);
        var store = new BackupStore(folder, BackupMode.OriginalPlusLastN, 50, () => now);
        store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("v0"));
        for (int i = 1; i <= 30; i++)
        {
            store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("v" + i));
            string newest = Directory.GetFiles(folder, "*_save.srt").OrderBy(f => f, StringComparer.Ordinal).Last();
            Assert.Equal(Content.Srt("v" + i), File.ReadAllBytes(newest));
        }
    }

    [Fact]
    public void Each_item_stream_and_file_has_its_own_original()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);

        Assert.Single(store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("a")));
        Assert.Single(store.BeforeSave(Item, 2, "/m/a.srt", Content.Srt("b")));
        Assert.Single(store.BeforeSave(Item, 1, "/m/other.srt", Content.Srt("c")));
        Assert.Single(store.BeforeSave("0123456789abcdef0123456789abcdef", 1, "/m/a.srt", Content.Srt("d")));
        Assert.Empty(store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("again")));

        Assert.Equal(4, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public void Many_backups_in_the_same_millisecond_never_overwrite_each_other()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalPlusLastN, 1000);

        for (int i = 0; i < 40; i++)
        {
            store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("version " + i));
        }

        string[] files = Directory.GetFiles(folder);
        Assert.Equal(40, files.Length);
        Assert.Equal(40, files.Select(File.ReadAllBytes).Select(Content.Hash).Distinct().Count());
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("not hex!")]
    public void An_item_id_that_is_not_hex_is_refused_before_anything_is_written(string itemId)
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);

        Assert.Throws<ArgumentException>(() => store.BeforeSave(itemId, 1, "/m/a.srt", Content.Srt("x")));

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void A_keep_count_below_one_is_treated_as_one()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalPlusLastN, 0);
        for (int i = 1; i <= 4; i++)
        {
            store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("v" + i));
        }

        Assert.Equal(2, Directory.GetFiles(folder).Length); // the original and one earlier version
    }

    [Fact]
    public void An_unwritable_backup_folder_is_reported_clearly()
    {
        if (UnixFileMetadata.CurrentUid() == 0)
        {
            Integration.SkippedBecause("running as root, which can write anywhere");
            return;
        }

        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(() => store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("x")));

        Assert.Equal(SubtitleWriteError.NotWritable, ex.Error);
        Assert.Contains(folder, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", ex.Message, StringComparison.Ordinal);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void A_backup_folder_that_is_really_a_file_is_an_io_error()
    {
        using var dir = new TempDir();
        string folder = dir.Write("bk", Array.Empty<byte>());
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);

        SubtitleWriteException ex = Assert.Throws<SubtitleWriteException>(() => store.BeforeSave(Item, 1, "/m/a.srt", Content.Srt("x")));

        Assert.Equal(SubtitleWriteError.Io, ex.Error);
    }

    [Fact]
    public void Restore_points_are_kept_whatever_the_mode_and_only_the_newest_three()
    {
        using var dir = new TempDir();
        string folder = Path.Combine(dir.Root, "bk");
        var store = new BackupStore(folder, BackupMode.OriginalOnly, 3);
        for (int i = 1; i <= 5; i++)
        {
            string path = store.KeepRestorePoint(Item, 2, "/m/a.srt", Content.Srt("r" + i));
            Assert.EndsWith("_restore.srt", path, StringComparison.Ordinal);
            Thread.Sleep(5); // distinct timestamps, so "newest" is well defined
        }

        string[] files = Directory.GetFiles(folder);
        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.EndsWith("_restore.srt", f, StringComparison.Ordinal));
        Assert.NotNull(store.FindByHash(Item, 2, "/m/a.srt", Content.Hash(Content.Srt("r5"))));
        Assert.NotNull(store.FindByHash(Item, 2, "/m/a.srt", Content.Hash(Content.Srt("r3"))));
        Assert.Null(store.FindByHash(Item, 2, "/m/a.srt", Content.Hash(Content.Srt("r2"))));
    }

    [Fact]
    public void Find_by_hash_finds_originals_saves_and_restore_points_of_that_subtitle_only()
    {
        using var dir = new TempDir();
        var store = new BackupStore(Path.Combine(dir.Root, "bk"), BackupMode.OriginalPlusLastN, 3);
        byte[] original = Content.Srt("o");
        byte[] earlier = Content.Srt("e");
        byte[] point = Content.Srt("p");
        store.BeforeSave(Item, 1, "/m/a.srt", original);
        store.BeforeSave(Item, 1, "/m/a.srt", earlier);
        store.KeepRestorePoint(Item, 1, "/m/a.srt", point);
        store.BeforeSave(Item, 1, "/m/b.srt", Content.Srt("other file"));

        Assert.Equal(original, store.FindByHash(Item, 1, "/m/a.srt", Content.Hash(original)));
        Assert.Equal(earlier, store.FindByHash(Item, 1, "/m/a.srt", " " + Content.Hash(earlier).ToUpperInvariant()));
        Assert.Equal(point, store.FindByHash(Item, 1, "/m/a.srt", Content.Hash(point)));
        Assert.Null(store.FindByHash(Item, 1, "/m/a.srt", Content.Hash(Content.Srt("other file"))));
        Assert.Null(store.FindByHash(Item, 11, "/m/a.srt", Content.Hash(original)));
        Assert.Null(new BackupStore(Path.Combine(dir.Root, "none"), BackupMode.OriginalOnly, 3).FindByHash(Item, 1, "/m/a.srt", Content.Hash(original)));
    }
}
