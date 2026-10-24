using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SubtitleQuickEdit.Core.Srt;
using Xunit;

namespace SubtitleQuickEdit.Core.Tests;

/// <summary>
/// Loading and saving without edits must be byte-identical, and an edit may only change the bytes of the
/// edited cue's text.
/// </summary>
public class SrtRoundTripTests
{
    public static IEnumerable<object[]> AllFixtures() => Fixtures.All().Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Apply_with_no_edits_returns_identical_bytes(Fixtures.Fixture fixture)
    {
        SrtDocument doc = SrtDocument.Parse(fixture.Data).Document!;

        SrtApplyResult result = doc.Apply(Array.Empty<SrtEdit>());

        Assert.Equal(fixture.Data, result.Content.ToArray());
        Assert.Equal(doc.Hash, result.Hash);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Replacing_every_cue_with_its_own_text_is_byte_identical(Fixtures.Fixture fixture)
    {
        SrtDocument doc = SrtDocument.Parse(fixture.Data).Document!;
        List<SrtEdit> edits = doc.Cues.Select(c => (SrtEdit)new SrtTextEdit(c.Position, c.Text, c.Text)).ToList();

        SrtApplyResult result = doc.Apply(edits);

        Assert.Equal(fixture.Data, result.Content.ToArray());
    }

    [Fact]
    public void Apply_with_no_edits_on_the_malformed_file_is_byte_identical()
    {
        byte[] data = Fixtures.Malformed();

        SrtApplyResult result = SrtDocument.Parse(data).Document!.Apply(Array.Empty<SrtEdit>());

        Assert.Equal(data, result.Content.ToArray());
    }

    [Fact]
    public void Apply_returns_a_copy_that_does_not_alias_the_documents_data()
    {
        byte[] data = Fixtures.Utf8(Fixtures.Render(Fixtures.Cues(Fixtures.English)));
        SrtDocument doc = SrtDocument.Parse(data).Document!;

        SrtApplyResult result = doc.Apply(Array.Empty<SrtEdit>());
        result.Content.ToArray()[0] = 0;

        Assert.Equal(data, doc.Apply(Array.Empty<SrtEdit>()).Content.ToArray());
    }

    [Fact]
    public void Random_files_round_trip_and_a_random_edit_only_changes_that_cues_text_bytes()
    {
        var random = new Random(20261016);
        for (int round = 0; round < 300; round++)
        {
            (byte[] data, string[] texts, bool polish) = RandomFile(random);
            SrtParseResult parsed = SrtDocument.Parse(data);
            Assert.True(parsed.Document is not null, $"round {round} (polish={polish}): {parsed.FailureReason}");
            SrtDocument doc = parsed.Document!;
            Assert.Equal(texts, doc.Cues.Select(c => c.Text).ToArray());
            Assert.Equal(data, doc.Apply(Array.Empty<SrtEdit>()).Content.ToArray());

            int pos = random.Next(1, doc.Cues.Count + 1);
            string newText = RandomText(random, polish);
            SrtCue cue = doc.Cues[pos - 1];
            SrtApplyResult edited = doc.Apply(new SrtEdit[] { new SrtTextEdit(pos, cue.Text, newText) });
            byte[] after = edited.Content.ToArray();

            int from = doc.ByteOffset(cue.Layout.TextStart);
            int to = doc.ByteOffset(cue.Layout.TextEnd);
            Assert.Equal(data[..from], after[..from]);
            Assert.Equal(data[to..], after[^(data.Length - to)..]);

            SrtDocument reparsed = SrtDocument.Parse(after).Document!;
            Assert.Equal(doc.Cues.Count, reparsed.Cues.Count);
            for (int i = 0; i < doc.Cues.Count; i++)
            {
                string expected = i == pos - 1 ? newText : doc.Cues[i].Text;
                Assert.Equal(expected, reparsed.Cues[i].Text);
                Assert.Equal(doc.Cues[i].StartMs, reparsed.Cues[i].StartMs);
                Assert.Equal(doc.Cues[i].EndMs, reparsed.Cues[i].EndMs);
            }
        }
    }

    private static string RandomText(Random random, bool polish)
    {
        int lines = random.Next(1, 4);
        var parts = new List<string>();
        for (int l = 0; l < lines; l++)
        {
            if (polish)
            {
                // Legacy code pages need natural text: the encoding detector cannot place random letters.
                parts.Add(Fixtures.Polish[random.Next(Fixtures.Polish.Length)].Replace("\n", " ", StringComparison.Ordinal));
                continue;
            }

            const string Alphabet = "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?'-üößÄÖÜéèñ";
            var sb = new StringBuilder();
            sb.Append((char)('A' + random.Next(26)));
            int len = random.Next(2, 30);
            for (int i = 0; i < len; i++)
            {
                sb.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            sb.Append('.');
            parts.Add(sb.ToString());
        }

        return string.Join("\n", parts);
    }

    private static (byte[] Data, string[] Texts, bool Polish) RandomFile(Random random)
    {
        int kind = random.Next(5);
        bool polish = kind == 3;
        int count = random.Next(1, 41);
        var texts = new string[count];
        var cues = new List<(long Start, long End, string Text)>();
        for (int i = 0; i < count; i++)
        {
            texts[i] = RandomText(random, polish);
            long start = 1000L + (i * 3000L) + random.Next(0, 500);
            cues.Add((start, start + 1000 + random.Next(0, 1500), texts[i]));
        }

        string eol = new[] { "\n", "\r\n", "\r" }[random.Next(3)];
        int tail = random.Next(3);
        string text = Fixtures.Render(cues, eol, numbers: true, gap: true, tail: tail);
        byte[] data = kind switch
        {
            0 => Fixtures.Utf8(text),
            1 => Fixtures.Utf8(text, bom: true),
            2 => Fixtures.Utf16Le(text),
            3 => Fixtures.Windows1250(text),
            _ => Fixtures.Utf16Be(text),
        };
        return (data, texts, polish);
    }
}
