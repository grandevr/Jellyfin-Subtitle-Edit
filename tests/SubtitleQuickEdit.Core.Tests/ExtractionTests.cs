using System;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Extraction;
using SubtitleQuickEdit.Core.Storage;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class ExtractedNamingTests
{
    [Theory]
    [InlineData("eng", "en")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("fre", "fr")]
    [InlineData("cze", "cs")]
    [InlineData("chi", "zh")]
    [InlineData("jpn", "ja")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" eng ", "en")]
    [InlineData("pt-BR", "pt")]
    [InlineData("zh_Hant", "zh")]
    [InlineData("tlh", "tlh")]
    [InlineData("und", null)]
    [InlineData("mul", null)]
    [InlineData("zxx", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("e", null)]
    [InlineData("engl", null)]
    [InlineData("e1", null)]
    [InlineData("../", null)]
    public void Languages_become_the_code_jellyfin_reads_from_a_file_name(string? language, string? expected)
    {
        Assert.Equal(expected, ExtractedNaming.LanguageCode(language));
    }

    [Theory]
    [InlineData("/cache/abc.ass", ".ass")]
    [InlineData("/cache/abc.SSA", ".ssa")]
    [InlineData("/cache/abc.srt", ".srt")]
    [InlineData("/cache/abc.vtt", ".vtt")]
    [InlineData("/cache/abc.sup", null)]
    [InlineData("/cache/abc", null)]
    public void The_extension_comes_from_the_file_jellyfin_extracted(string extracted, string? expected)
    {
        Assert.Equal(expected, ExtractedNaming.Extension(extracted));
    }

    [Theory]
    [InlineData("jpn", TrackDisposition.None, ".ass", "Ep 01.ja.ass")]
    [InlineData("eng", TrackDisposition.Forced, ".srt", "Ep 01.en.forced.srt")]
    [InlineData("eng", TrackDisposition.HearingImpaired, ".srt", "Ep 01.en.sdh.srt")]
    [InlineData("ger", TrackDisposition.Default | TrackDisposition.Forced | TrackDisposition.HearingImpaired, ".vtt", "Ep 01.de.default.forced.sdh.vtt")]
    [InlineData(null, TrackDisposition.None, ".srt", "Ep 01.srt")]
    [InlineData("und", TrackDisposition.Forced, ".ssa", "Ep 01.forced.ssa")]
    public void The_file_is_named_after_the_video_with_language_and_flags(string? language, TrackDisposition disposition, string extension, string expected)
    {
        string target = ExtractedNaming.TargetPath("/media/Show/Season 1/Ep 01.mkv", language, disposition, extension);

        Assert.Equal(Path.Combine("/media/Show/Season 1", expected), target);
    }

    [Fact]
    public void Only_text_subtitle_extensions_are_accepted()
    {
        Assert.Throws<ArgumentException>(() => ExtractedNaming.TargetPath("/media/a.mkv", "eng", TrackDisposition.None, ".sup"));
        Assert.Throws<ArgumentException>(() => ExtractedNaming.TargetPath("/media/a.mkv", "eng", TrackDisposition.None, ".SRT"));
    }

    [Fact]
    public void A_name_too_long_for_the_file_system_is_refused()
    {
        string video = "/media/" + new string('a', 245) + ".mkv";

        Assert.Throws<ArgumentException>(() => ExtractedNaming.TargetPath(video, "eng", TrackDisposition.Forced, ".srt"));
        Assert.EndsWith(".srt", ExtractedNaming.TargetPath("/media/" + new string('a', 248) + ".mkv", null, TrackDisposition.None, ".srt"), StringComparison.Ordinal);
    }
}

public class SafeFileWriterCreateTests
{
    private static readonly byte[] New = Content.Srt("new");
    private static readonly byte[] Other = Content.Srt("someone else's file");

    [Fact]
    public void A_new_file_is_created_and_nothing_is_left_behind()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "ep.en.srt");

        WriteOutcome outcome = new SafeFileWriter().Create(path, New, null);

        Assert.Equal(WriteMethod.Created, outcome.Method);
        Assert.Equal(Content.Hash(New), outcome.NewHash);
        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Equal(new[] { "ep.en.srt" }, dir.AllFiles());
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void An_existing_file_is_never_touched()
    {
        using var dir = new TempDir();
        string path = dir.Write("ep.en.srt", Other);

        var ex = Assert.Throws<SubtitleWriteException>(() => new SafeFileWriter().Create(path, New, null));

        Assert.Equal(SubtitleWriteError.AlreadyExists, ex.Error);
        Assert.Equal(Content.Hash(Other), ex.CurrentHash);
        Assert.Equal(Other, File.ReadAllBytes(path));
        Assert.Equal(new[] { "ep.en.srt" }, dir.AllFiles());
    }

    [Fact]
    public void A_file_that_appears_while_writing_is_not_overwritten()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "ep.en.srt");
        var racer = new ActionAt(WriteStage.BeforeRename, () => File.WriteAllBytes(path, Other));

        var ex = Assert.Throws<SubtitleWriteException>(() => new SafeFileWriter(observer: racer).Create(path, New, null));

        Assert.Equal(SubtitleWriteError.AlreadyExists, ex.Error);
        Assert.Equal(Other, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead)]
    public void The_new_file_gets_the_given_owner_group_and_mode(UnixFileMode mode)
    {
        using var dir = new TempDir();
        string reference = dir.Write("ep.mkv", Other, mode);
        FileOwnership ownership = new UnixFileMetadata().Get(reference)!.Value;
        string path = Path.Combine(dir.Root, "ep.en.srt");

        WriteOutcome outcome = new SafeFileWriter().Create(path, New, ownership);

        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.Equal(ownership, new UnixFileMetadata().Get(path));
        Assert.Empty(outcome.Notes);
    }

    [Fact]
    public void An_owner_that_cannot_be_matched_keeps_the_file_and_says_so()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "ep.en.srt");
        var ownership = new FileOwnership(0, 0, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        WriteOutcome outcome = new SafeFileWriter(new RefusingMetadata()).Create(path, New, ownership);

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Contains(outcome.Notes, n => n.Contains("could not be set to match", StringComparison.Ordinal));
        Assert.Empty(dir.WriterFiles());
    }

    [Fact]
    public void The_temp_file_is_synced_before_the_move_and_the_folder_after_it()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "ep.en.srt");
        var durability = new RecordingDurability(() => File.Exists(path) ? "exists" : "missing");

        new SafeFileWriter(durability: durability).Create(path, New, null);

        Assert.Equal(new[] { ("file", "missing"), ("dir", "exists") }, durability.Events.Select(e => (e.Kind, e.State)).ToArray());
    }

    [Fact]
    public void A_crash_before_the_move_leaves_no_file_and_the_next_create_cleans_up()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Root, "ep.en.srt");

        Assert.Throws<SimulatedCrashException>(() => new SafeFileWriter(observer: new CrashAt(WriteStage.BeforeRename)).Create(path, New, null));
        Assert.False(File.Exists(path));
        Assert.Single(dir.WriterFiles());

        WriteOutcome outcome = new SafeFileWriter().Create(path, New, null);

        Assert.Equal(New, File.ReadAllBytes(path));
        Assert.Empty(dir.WriterFiles());
        Assert.Contains(outcome.Notes, n => n.Contains("stale temporary file", StringComparison.Ordinal));
    }
}

public class SubtitleExtractorTests
{
    private const string Ass =
        "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,20\n\n"
        + "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}Hello{\\i0}\n";

    private static readonly byte[] Extracted = Content.Srt("extracted");
    private static readonly byte[] Existing = Content.Srt("existing file, a little longer");
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_track_is_written_next_to_the_video_and_recorded()
    {
        using var env = new Env();

        ExtractResult result = env.Extractor.Extract(env.Request(Extracted));

        string target = Path.Combine(env.Media, "Ep 01.en.forced.srt");
        Assert.Equal(Extracted, File.ReadAllBytes(target));
        Assert.Null(result.BackupPath);
        ExtractedFile record = Assert.Single(env.Store.All());
        Assert.Equal(result.Record, record);
        Assert.Equal(target, record.Path);
        Assert.Equal(Content.Hash(Extracted), record.Hash);
        Assert.Equal("0123456789abcdef0123456789abcdef", record.ItemId);
        Assert.Equal(3, record.SourceStreamIndex);
        Assert.Equal("u1", record.UserId);
        Assert.Equal("Dana", record.UserName);
        Assert.Equal(Now, record.CreatedUtc);
        Assert.False(record.Replaced);
    }

    [Fact]
    public void The_new_file_gets_the_videos_mode_without_execute_bits()
    {
        using var env = new Env(videoMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        env.Extractor.Extract(env.Request(Extracted));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            File.GetUnixFileMode(Path.Combine(env.Media, "Ep 01.en.forced.srt")));
    }

    [Fact]
    public void An_ass_track_stays_ass()
    {
        using var env = new Env();

        ExtractResult result = env.Extractor.Extract(env.Request(Fixtures.Utf8(Ass), "/cache/track.ass", "jpn", TrackDisposition.None));

        Assert.Equal(Path.Combine(env.Media, "Ep 01.ja.ass"), result.Record.Path);
        Assert.Equal(Ass, File.ReadAllText(result.Record.Path));
    }

    [Theory]
    [InlineData("/cache/track.sup", ExtractionError.Unsupported)]
    [InlineData("/cache/track.ass", ExtractionError.NotParsable)]
    public void Content_that_cannot_be_edited_is_refused_and_nothing_is_written(string extractedPath, ExtractionError expected)
    {
        using var env = new Env();

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Extract(env.Request(Extracted, extractedPath)));

        Assert.Equal(expected, ex.Error);
        Assert.Equal(new[] { "Ep 01.mkv" }, env.MediaFiles());
        Assert.Empty(env.Store.All());
    }

    [Fact]
    public void A_track_too_large_to_edit_is_refused()
    {
        using var env = new Env();
        byte[] huge = new byte[Subtitles.SubtitleLimits.MaxFileBytes + 1];

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Extract(env.Request(huge)));

        Assert.Equal(ExtractionError.TooLarge, ex.Error);
        Assert.Equal(new[] { "Ep 01.mkv" }, env.MediaFiles());
    }

    [Fact]
    public void An_existing_file_is_kept_unless_replacing_was_asked_for()
    {
        using var env = new Env();
        string target = env.Put("Ep 01.en.forced.srt", Existing);

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Extract(env.Request(Extracted, isAdmin: true)));

        Assert.Equal(ExtractionError.FileExists, ex.Error);
        Assert.Equal(Content.Hash(Existing), ex.CurrentHash);
        Assert.Equal(Existing, File.ReadAllBytes(target));
        Assert.Empty(env.Store.All());
        Assert.Empty(env.BackupFiles());
    }

    [Fact]
    public void Only_an_admin_may_replace()
    {
        using var env = new Env();
        string target = env.Put("Ep 01.en.forced.srt", Existing);
        ExtractRequest request = env.Request(Extracted) with { Replace = true, ExpectedExistingHash = Content.Hash(Existing) };

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Extract(request));

        Assert.Equal(ExtractionError.ReplaceNotAllowed, ex.Error);
        Assert.Equal(Existing, File.ReadAllBytes(target));
        Assert.Empty(env.BackupFiles());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void Replacing_needs_the_hash_of_the_file_the_admin_saw(string? hash)
    {
        using var env = new Env();
        string target = env.Put("Ep 01.en.forced.srt", Existing);
        ExtractRequest request = env.Request(Extracted, isAdmin: true) with { Replace = true, ExpectedExistingHash = hash };

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Extract(request));

        Assert.Equal(ExtractionError.FileChanged, ex.Error);
        Assert.Equal(Content.Hash(Existing), ex.CurrentHash);
        Assert.Equal(Existing, File.ReadAllBytes(target));
        Assert.Empty(env.BackupFiles());
    }

    [Fact]
    public void An_admin_replace_keeps_the_old_file_in_the_backups()
    {
        using var env = new Env();
        string target = env.Put("Ep 01.en.forced.srt", Existing);
        ExtractRequest request = env.Request(Extracted, isAdmin: true) with
        {
            Replace = true, ExpectedExistingHash = Content.Hash(Existing), ExistingStreamIndex = 5,
        };

        ExtractResult result = env.Extractor.Extract(request);

        Assert.Equal(Extracted, File.ReadAllBytes(target));
        Assert.NotNull(result.BackupPath);
        Assert.EndsWith("_replaced.srt", result.BackupPath, StringComparison.Ordinal);
        Assert.StartsWith("0123456789abcdef0123456789abcdef_5_", Path.GetFileName(result.BackupPath), StringComparison.Ordinal);
        Assert.Equal(Existing, File.ReadAllBytes(result.BackupPath));
        Assert.True(result.Record.Replaced);
    }

    [Fact]
    public void A_file_changed_between_the_check_and_the_write_is_not_replaced()
    {
        using var env = new Env();
        string target = env.Put("Ep 01.en.forced.srt", Existing);
        byte[] meanwhile = Content.Srt("changed meanwhile");
        var writer = new SafeFileWriter(observer: new ActionAt(WriteStage.TempVerified, () => File.WriteAllBytes(target, meanwhile)));
        var extractor = new SubtitleExtractor(writer, env.Backups, env.Store, utcNow: () => Now);
        ExtractRequest request = env.Request(Extracted, isAdmin: true) with { Replace = true, ExpectedExistingHash = Content.Hash(Existing) };

        var ex = Assert.Throws<ExtractionException>(() => extractor.Extract(request));

        Assert.Equal(ExtractionError.FileChanged, ex.Error);
        Assert.Equal(Content.Hash(meanwhile), ex.CurrentHash);
        Assert.Equal(meanwhile, File.ReadAllBytes(target));
    }

    [Fact]
    public void Extracting_again_over_an_earlier_copy_keeps_one_record()
    {
        using var env = new Env();
        ExtractedFile first = env.Extractor.Extract(env.Request(Extracted)).Record;
        ExtractRequest again = env.Request(Content.Srt("second"), isAdmin: true) with { Replace = true, ExpectedExistingHash = first.Hash };

        ExtractedFile second = env.Extractor.Extract(again).Record;

        Assert.Equal(second, Assert.Single(env.Store.All()));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void A_plan_shows_the_target_and_what_is_there_without_writing()
    {
        using var env = new Env();
        ExtractedFile earlier = env.Extractor.Extract(env.Request(Existing)).Record;

        ExtractPlan plan = env.Extractor.Plan(env.Request(Extracted));

        Assert.Equal(earlier.Path, plan.TargetPath);
        Assert.Equal(Subtitles.SubtitleFormat.Srt, plan.Format);
        Assert.Equal(Extracted, plan.Content.ToArray());
        Assert.Equal(Existing, plan.ExistingContent!.Value.ToArray());
        Assert.Equal(Content.Hash(Existing), plan.ExistingHash);
        Assert.Equal(earlier, plan.ExistingRecord);
        Assert.Equal(Existing, File.ReadAllBytes(earlier.Path));
    }

    [Fact]
    public void A_plan_for_a_free_name_has_no_existing_file()
    {
        using var env = new Env();

        ExtractPlan plan = env.Extractor.Plan(env.Request(Extracted));

        Assert.Null(plan.ExistingContent);
        Assert.Null(plan.ExistingHash);
        Assert.Null(plan.ExistingRecord);
    }

    [Fact]
    public void Deleting_an_extracted_file_keeps_its_last_content_in_the_backups()
    {
        using var env = new Env();
        ExtractedFile record = env.Extractor.Extract(env.Request(Extracted)).Record;
        byte[] edited = Content.Srt("edited after extraction");
        File.WriteAllBytes(record.Path, edited);

        DeleteExtractedResult result = env.Extractor.Delete(record.Id, 7);

        Assert.False(File.Exists(record.Path));
        Assert.Equal(new[] { "Ep 01.mkv" }, env.MediaFiles());
        Assert.Empty(env.Store.All());
        Assert.Equal(record, result.Record);
        Assert.EndsWith("_deleted.srt", result.BackupPath, StringComparison.Ordinal);
        Assert.StartsWith("0123456789abcdef0123456789abcdef_7_", Path.GetFileName(result.BackupPath), StringComparison.Ordinal);
        Assert.Equal(edited, File.ReadAllBytes(result.BackupPath!));
    }

    [Fact]
    public void Deleting_a_record_whose_file_is_gone_just_removes_the_record()
    {
        using var env = new Env();
        ExtractedFile record = env.Extractor.Extract(env.Request(Extracted)).Record;
        File.Delete(record.Path);

        DeleteExtractedResult result = env.Extractor.Delete(record.Id, -1);

        Assert.Null(result.BackupPath);
        Assert.Empty(env.Store.All());
    }

    [Theory]
    [InlineData("ffffffffffffffffffffffffffffffff")]
    [InlineData("../../etc/passwd")]
    public void Deleting_an_unknown_record_is_refused(string id)
    {
        using var env = new Env();

        var ex = Assert.Throws<ExtractionException>(() => env.Extractor.Delete(id, -1));

        Assert.Equal(ExtractionError.NotFound, ex.Error);
    }

    [Fact]
    public void The_store_skips_unreadable_records_and_lists_newest_first()
    {
        using var env = new Env();
        var times = new[] { Now, Now.AddMinutes(5) };
        int call = 0;
        var extractor = new SubtitleExtractor(new SafeFileWriter(), env.Backups, env.Store, utcNow: () => times[call++]);
        ExtractedFile older = extractor.Extract(env.Request(Extracted)).Record;
        ExtractedFile newer = extractor.Extract(env.Request(Fixtures.Utf8(Ass), "/cache/t.ass", "jpn", TrackDisposition.None)).Record;
        File.WriteAllText(Path.Combine(env.Data, "0123456789abcdef0123456789abcdef.json"), "{ not json");
        File.WriteAllText(Path.Combine(env.Data, "notanid.json"), "{}");

        Assert.Equal(new[] { newer.Id, older.Id }, env.Store.All().Select(r => r.Id).ToArray());
        Assert.Equal(older, env.Store.Find(older.Id));
        Assert.Null(env.Store.Find("0123456789abcdef0123456789abcdef"));
    }

    private sealed class Env : IDisposable
    {
        private readonly TempDir dir = new();

        public Env(UnixFileMode videoMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
        {
            Media = Path.Combine(dir.Root, "media");
            Data = Path.Combine(dir.Root, "extracted");
            Directory.CreateDirectory(Media);
            Video = Path.Combine(Media, "Ep 01.mkv");
            File.WriteAllBytes(Video, [1, 2, 3]);
            File.SetUnixFileMode(Video, videoMode);
            Backups = new BackupStore(Path.Combine(dir.Root, "backups"), BackupMode.OriginalOnly, 5);
            Store = new ExtractedStore(Data);
            Extractor = new SubtitleExtractor(new SafeFileWriter(), Backups, Store, utcNow: () => Now);
        }

        public string Media { get; }

        public string Data { get; }

        public string Video { get; }

        public BackupStore Backups { get; }

        public ExtractedStore Store { get; }

        public SubtitleExtractor Extractor { get; }

        public ExtractRequest Request(
            byte[] content,
            string extractedPath = "/cache/track.srt",
            string? language = "eng",
            TrackDisposition disposition = TrackDisposition.Forced,
            bool isAdmin = false) => new()
        {
            ItemId = "0123456789abcdef0123456789abcdef",
            SourceStreamIndex = 3,
            VideoPath = Video,
            ExtractedPath = extractedPath,
            Content = content,
            Language = language,
            Disposition = disposition,
            UserId = "u1",
            UserName = "Dana",
            IsAdmin = isAdmin,
        };

        public string Put(string name, byte[] content)
        {
            string path = Path.Combine(Media, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string[] MediaFiles() => Directory.GetFiles(Media).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

        public string[] BackupFiles()
        {
            string folder = Path.Combine(dir.Root, "backups");
            return Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
        }

        public void Dispose() => dir.Dispose();
    }
}
