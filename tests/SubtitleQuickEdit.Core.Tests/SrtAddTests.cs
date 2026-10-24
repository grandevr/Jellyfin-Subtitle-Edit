using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Srt;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

public class SrtAddTests
{
    public static IEnumerable<object[]> AllFixtures() => Fixtures.All().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void An_added_cue_lands_in_start_order_and_everything_is_renumbered(Fixtures.Fixture fixture)
    {
        SrtDocument doc = SrtDocument.Parse(fixture.Data).Document!;
        long start = doc.Cues[3].StartMs + 500; // between cue 4 and cue 5

        SrtApplyResult result = doc.Apply(new SrtEdit[] { new SrtAddCue("a1", start, start + 1500, "A new line") });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(doc.Cues.Count + 1, after.Cues.Count);
        Assert.Equal(5, result.AddedCuePositions["a1"]);
        Assert.Equal("A new line", after.Cues[4].Text);
        Assert.Equal(start, after.Cues[4].StartMs);
        Assert.Equal(start + 1500, after.Cues[4].EndMs);
        Assert.Equal(Enumerable.Range(1, after.Cues.Count), after.Cues.Select(c => c.Number!.Value));
        AssertOriginalCuesUnchanged(doc, after, skipPositions: new[] { 5 });
        Assert.Equal(fixture.Eol, after.LineEnding);
        Assert.Equal(fixture.BomLength, after.Encoding.BomLength);
    }

    [Fact]
    public void A_cue_before_the_first_goes_first_and_a_bom_stays_in_front()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.German), "\r\n"), bom: true);
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SrtEdit[] { new SrtAddCue("x", 100, 900, "Vorspann") }).Content.ToArray();

        Assert.Equal(Fixtures.Utf8Bom, after[..3]);
        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal("Vorspann", reparsed.Cues[0].Text);
        Assert.Equal(1, reparsed.Cues[0].Number);
        Assert.Equal(doc.Cues[0].Text, reparsed.Cues[1].Text);
        string text = Encoding.UTF8.GetString(after, 3, after.Length - 3);
        Assert.StartsWith("1\r\n00:00:00,100 --> 00:00:00,900\r\nVorspann\r\n\r\n2\r\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_cue_after_the_last_keeps_the_files_ending_style(int tail)
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English), tail: tail));
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        long start = doc.Cues[^1].EndMs + 1000;

        byte[] after = doc.Apply(new SrtEdit[] { new SrtAddCue("end", start, start + 1000, "The very end") }).Content.ToArray();

        string text = Encoding.UTF8.GetString(after);
        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal(13, reparsed.Cues.Count);
        Assert.Equal("The very end", reparsed.Cues[12].Text);
        Assert.Equal("End of test.", reparsed.Cues[11].Text);
        Assert.Equal(13, reparsed.Cues[12].Number);
        Assert.Equal(new string('\n', tail), text[text.TrimEnd('\n').Length..]);
        Assert.EndsWith("The very end" + new string('\n', tail), text, StringComparison.Ordinal);
        Assert.Contains("End of test.\n\n13\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cue_with_the_same_start_as_an_existing_one_goes_after_it()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;
        long start = doc.Cues[2].StartMs;

        SrtApplyResult result = doc.Apply(new SrtEdit[] { new SrtAddCue("same", start, start + 500, "Twin") });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(doc.Cues[2].Text, after.Cues[2].Text);
        Assert.Equal("Twin", after.Cues[3].Text);
        Assert.Equal(4, result.AddedCuePositions["same"]);
    }

    [Fact]
    public void Several_added_cues_are_ordered_by_start_and_mapped_by_client_id()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;
        long gap = doc.Cues[1].EndMs + 200; // after cue 2, before cue 3

        SrtApplyResult result = doc.Apply(
            new SrtEdit[]
            {
                new SrtAddCue("late", gap + 600, gap + 900, "Second of the pair"),
                new SrtAddCue("first", 50, 400, "Before everything"),
                new SrtAddCue("early", gap, gap + 400, "First of the pair"),
                new SrtAddCue("last", 999_000, 1_000_000, "After everything"),
            });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(16, after.Cues.Count);
        Assert.Equal(1, result.AddedCuePositions["first"]);
        Assert.Equal(4, result.AddedCuePositions["early"]);
        Assert.Equal(5, result.AddedCuePositions["late"]);
        Assert.Equal(16, result.AddedCuePositions["last"]);
        Assert.Equal("Before everything", after.Cues[0].Text);
        Assert.Equal("First of the pair", after.Cues[3].Text);
        Assert.Equal("Second of the pair", after.Cues[4].Text);
        Assert.Equal("After everything", after.Cues[15].Text);
        Assert.Equal(Enumerable.Range(1, 16), after.Cues.Select(c => c.Number!.Value));
        Assert.Equal(
            after.Cues.Select(c => c.StartMs).OrderBy(x => x),
            after.Cues.Select(c => c.StartMs));
    }

    [Fact]
    public void Cues_in_a_gap_less_malformed_file_get_proper_separation_and_numbers()
    {
        byte[] data = Fixtures.Malformed();
        SrtDocument doc = SrtDocument.Parse(data).Document!;
        long start = doc.Cues[1].StartMs + 100;

        SrtApplyResult result = doc.Apply(new SrtEdit[] { new SrtAddCue("m", start, start + 1000, "Inserted") });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(13, after.Cues.Count);
        Assert.Equal("Inserted", after.Cues[2].Text);
        Assert.Equal(Enumerable.Range(1, 13), after.Cues.Select(c => c.Number!.Value));
        Assert.Equal(doc.Cues[1].Text, after.Cues[1].Text);
        Assert.Equal(doc.Cues[2].Text, after.Cues[3].Text);
        AssertOriginalCuesUnchanged(doc, after, skipPositions: new[] { 3 });
    }

    [Fact]
    public void Adding_to_a_cp1250_file_stores_polish_characters_in_cp1250()
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Polish), "\r\n"));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SrtEdit[] { new SrtAddCue("p", 1, 1500, "Źdźbło łączy żółć") }).Content.ToArray();

        Assert.Equal("Źdźbło łączy żółć", SrtDocument.Parse(after).Document!.Cues[0].Text);
        Assert.True(after.AsSpan().IndexOf(Fixtures.Cp1250.GetBytes("Źdźbło łączy żółć\r\n")) >= 0);
    }

    [Fact]
    public void Adding_to_a_utf16_file_writes_utf16()
    {
        byte[] data = Fixtures.Utf16Be(Fixtures.Render(Fixtures.Cues(Fixtures.German)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SrtEdit[] { new SrtAddCue("u", 60_000, 61_000, "Zeile\nzwei") }).Content.ToArray();

        Assert.Equal(Fixtures.Utf16BeBom, after[..2]);
        SrtDocument reparsed = SrtDocument.Parse(after).Document!;
        Assert.Equal("utf-16be", reparsed.Encoding.Name);
        Assert.Equal("Zeile\nzwei", reparsed.Cues.Single(c => c.StartMs == 60_000).Text);
    }

    [Fact]
    public void In_a_file_with_out_of_order_cues_a_new_cue_goes_before_the_first_cue_that_starts_later()
    {
        string text = "1\n00:00:10,000 --> 00:00:11,000\nA\n\n2\n00:00:02,000 --> 00:00:03,000\nB\n\n3\n00:00:20,000 --> 00:00:21,000\nC\n";
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(text)).Document!;

        SrtApplyResult result = doc.Apply(new SrtEdit[] { new SrtAddCue("n", 5000, 6000, "N") });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(new[] { "N", "A", "B", "C" }, after.Cues.Select(c => c.Text).ToArray());
        Assert.Equal(1, result.AddedCuePositions["n"]);
    }

    [Fact]
    public void An_edit_and_an_add_together_report_shifted_positions()
    {
        SrtDocument doc = SrtDocument.Parse(Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)))).Document!;

        SrtApplyResult result = doc.Apply(
            new SrtEdit[]
            {
                new SrtTextEdit(3, doc.Cues[2].Text, "Third, edited"),
                new SrtAddCue("a", 100, 800, "Opening"),
            });

        SrtDocument after = SrtDocument.Parse(result.Content.ToArray()).Document!;
        Assert.Equal(new[] { 4 }, result.EditedCuePositions);
        Assert.Equal("Third, edited", after.Cues[3].Text);
        Assert.Equal(1, result.AddedCuePositions["a"]);
        Assert.Equal("Opening", after.Cues[0].Text);
    }

    [Fact]
    public void New_text_with_a_character_the_encoding_cannot_hold_is_rejected()
    {
        byte[] data = Fixtures.Windows1250(Fixtures.Render(Fixtures.Cues(Fixtures.Czech)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SrtEditException ex = Assert.Throws<SrtEditException>(
            () => doc.Apply(new SrtEdit[] { new SrtAddCue("c", 100, 900, "Привет") }));

        Assert.Equal(SrtEditError.Unrepresentable, ex.Error);
    }

    [Fact]
    public void Existing_timestamps_and_texts_are_untouched_when_adding()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.German), "\r\n"), bom: true);
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        byte[] after = doc.Apply(new SrtEdit[] { new SrtAddCue("t", 22_000, 23_000, "Mitte") }).Content.ToArray();

        // Strip the added block and renumbering: every original time line and text line must still be there, in order.
        string original = Encoding.UTF8.GetString(data, 3, data.Length - 3);
        string edited = Encoding.UTF8.GetString(after, 3, after.Length - 3);
        string[] originalLines = original.Split("\r\n").Where(l => l.Length > 0 && !IsNumber(l)).ToArray();
        string[] editedLines = edited.Split("\r\n").Where(l => l.Length > 0 && !IsNumber(l)).ToList()
            .Where(l => l is not "Mitte" && !l.StartsWith("00:00:22,000 --> 00:00:23,000", StringComparison.Ordinal)).ToArray();
        Assert.Equal(originalLines, editedLines);
    }

    private static bool IsNumber(string line) => line.All(char.IsAsciiDigit);

    private static void AssertOriginalCuesUnchanged(SrtDocument before, SrtDocument after, int[] skipPositions)
    {
        var originals = new Queue<SrtCue>(before.Cues);
        foreach (SrtCue cue in after.Cues)
        {
            if (skipPositions.Contains(cue.Position))
            {
                continue;
            }

            SrtCue original = originals.Dequeue();
            Assert.Equal(original.Text, cue.Text);
            Assert.Equal(original.StartMs, cue.StartMs);
            Assert.Equal(original.EndMs, cue.EndMs);
        }

        Assert.Empty(originals);
    }
}
