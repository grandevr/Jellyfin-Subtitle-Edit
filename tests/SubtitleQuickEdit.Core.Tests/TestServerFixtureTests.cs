using System;
using System.IO;
using System.Linq;
using SubtitleQuickEdit.Core.Srt;
using SubtitleQuickEdit.Core.Subtitles;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

/// <summary>
/// Runs against the actual .srt files on the throwaway Jellyfin test server, when
/// <c>SQE_FIXTURE_DIR</c> points at them. Skipped (passes trivially) otherwise.
/// </summary>
public class TestServerFixtureTests
{
    private static string[] Files()
    {
        string? dir = Environment.GetEnvironmentVariable("SQE_FIXTURE_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        // Episodes 1 and 2 hold the ten standard fixtures; episode 3's files are deliberate odd cases for the API tests.
        return Directory.GetFiles(dir, "*.srt", SearchOption.AllDirectories)
            .Where(f => f.Contains("S01E01", StringComparison.Ordinal) || f.Contains("S01E02", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void Every_test_server_subtitle_parses_round_trips_and_takes_an_edit_and_an_added_line()
    {
        string[] files = Files();
        if (files.Length == 0)
        {
            Integration.SkippedBecause("SQE_FIXTURE_DIR is not set");
            return;
        }

        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            byte[] data = File.ReadAllBytes(file);
            SrtParseResult parsed = SrtDocument.Parse(data);
            Assert.True(parsed.Document is not null, Path.GetFileName(file) + ": " + parsed.FailureReason);
            SrtDocument doc = parsed.Document!;
            Assert.Equal(12, doc.Cues.Count);

            Assert.Equal(data, doc.Apply(Array.Empty<SubtitleEdit>()).Content.ToArray());

            SubtitleApplyResult result = doc.Apply(
                new SubtitleEdit[]
                {
                    new SubtitleTextEdit(2, doc.Cues[1].Text, "Edited by the test."),
                    new SubtitleAddCue("n", 100, 900, "Added by the test."),
                });
            SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
            Assert.Equal(13, after.Cues.Count);
            Assert.Equal("Added by the test.", after.Cues[0].Text);
            Assert.Equal("Edited by the test.", after.Cues[2].Text);
            Assert.Equal(doc.Encoding.Name, after.Encoding.Name);
            Assert.Equal(doc.Encoding.BomLength, after.Encoding.BomLength);
        }
    }

    [Fact]
    public void The_windows_1250_test_server_files_decode_to_the_expected_polish_and_czech_text()
    {
        string[] files = Files();
        if (files.Length == 0)
        {
            Integration.SkippedBecause("SQE_FIXTURE_DIR is not set");
            return;
        }

        SrtDocument pl = SrtDocument.Parse(File.ReadAllBytes(files.Single(f => f.EndsWith("S01E01.pl.srt", StringComparison.Ordinal)))).Document!;
        SrtDocument cs = SrtDocument.Parse(File.ReadAllBytes(files.Single(f => f.EndsWith("S01E02.cs.srt", StringComparison.Ordinal)))).Document!;

        Assert.Equal(Fixtures.Polish, pl.Cues.Select(c => c.Text).ToArray());
        Assert.Equal(Fixtures.Czech, cs.Cues.Select(c => c.Text).ToArray());
    }
}
