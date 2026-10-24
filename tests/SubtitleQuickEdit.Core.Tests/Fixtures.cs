using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SubtitleQuickEdit.Core.Tests;

/// <summary>
/// Builds .srt test files in code: every encoding, line ending and malformation the spec lists.
/// </summary>
public static class Fixtures
{
    internal static readonly string[] English =
    {
        "Hello there.",
        "This is a two-line cue\nwith a line break.",
        "Did you hear that?",
        "I don't know what to say.",
        "Let's get out of here before it's too late.",
        "This is a three-line cue\nthat keeps going\nand going.",
        "Goodbye, my friend.",
        "See you tomorrow.",
        "I hope this works.",
        "Look at that star.",
        "Let's go home.",
        "End of test.",
    };

    internal static readonly string[] Polish =
    {
        "Zażółć gęślą jaźń.",
        "Pchnąć w tę łódź jeża\nlub ośm skrzyń fig.",
        "Dzień dobry, wszystko w porządku?",
        "Nie wiem, co powiedzieć.",
        "Chodźmy stąd, zanim będzie za późno.",
        "To jest wiersz\nz trzech linii\nw jednej kwestii.",
        "Żegnaj, przyjacielu.",
        "Do zobaczenia jutro.",
        "Mam nadzieję, że się uda.",
        "Spójrz na tę gwiazdę.",
        "Wracajmy do domu.",
        "Koniec testu.",
    };

    internal static readonly string[] German =
    {
        "Grüß dich, wie geht's?",
        "Schöne Straße\nund große Bäume.",
        "Hörst du das?",
        "Ich weiß nicht, was ich sagen soll.",
        "Lass uns gehen, bevor es zu spät ist.",
        "Das ist ein Satz über Äpfel,\nÖl und Übermut.",
        "Auf Wiedersehen, mein Freund.",
        "Bis morgen.",
        "Ich hoffe, es klappt.",
        "Schau dir den Stern an.",
        "Gehen wir nach Hause.",
        "Ende des Tests.",
    };

    internal static readonly string[] Czech =
    {
        "Příliš žluťoučký kůň úpěl ďábelské ódy.",
        "Dobrý den,\njak se máte?",
        "Slyšel jsi to?",
        "Nevím, co říct.",
        "Pojďme pryč, než bude pozdě.",
        "Řekni mi, kde je\nnejbližší nádraží.",
        "Sbohem, příteli.",
        "Uvidíme se zítra.",
        "Doufám, že to vyjde.",
        "Podívej se na tu hvězdu.",
        "Pojďme domů.",
        "Konec testu.",
    };

    internal static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    internal static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };

    internal static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };

    static Fixtures()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    internal static Encoding Cp1250 => Encoding.GetEncoding(1250);

    internal static List<(long Start, long End, string Text)> Cues(IEnumerable<string> texts) =>
        texts.Select((t, i) => (Start: 2000L + (i * 4500L), End: 2000L + (i * 4500L) + 3000L, Text: t)).ToList();

    internal static string Time(long ms) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:D2}:{(ms / 60_000) % 60:D2}:{(ms / 1000) % 60:D2},{ms % 1000:D3}");

    /// <summary>
    /// Renders cues as .srt text.
    /// </summary>
    /// <param name="cues">The cues.</param>
    /// <param name="eol">The line ending.</param>
    /// <param name="numbers">Whether to write number lines.</param>
    /// <param name="gap">Whether a blank line separates cues.</param>
    /// <param name="tail">How many line endings end the file (0, 1 or 2).</param>
    /// <returns>The text.</returns>
    internal static string Render(
        IReadOnlyList<(long Start, long End, string Text)> cues,
        string eol = "\n",
        bool numbers = true,
        bool gap = true,
        int tail = 2)
    {
        var blocks = new List<string>();
        for (int i = 0; i < cues.Count; i++)
        {
            string block = string.Empty;
            if (numbers)
            {
                block += (i + 1).ToString(CultureInfo.InvariantCulture) + eol;
            }

            block += Time(cues[i].Start) + " --> " + Time(cues[i].End) + eol + cues[i].Text.Replace("\n", eol, StringComparison.Ordinal);
            blocks.Add(block);
        }

        string text = string.Join(gap ? eol + eol : eol, blocks);
        for (int i = 0; i < tail; i++)
        {
            text += eol;
        }

        return text;
    }

    internal static byte[] Bytes(string text, Encoding encoding, byte[]? bom = null)
    {
        byte[] body = encoding.GetBytes(text);
        return bom is null ? body : bom.Concat(body).ToArray();
    }

    internal static byte[] Utf8(string text, bool bom = false) =>
        Bytes(text, new UTF8Encoding(false), bom ? Utf8Bom : null);

    internal static byte[] Utf16Le(string text) => Bytes(text, new UnicodeEncoding(false, false), Utf16LeBom);

    internal static byte[] Utf16Be(string text) => Bytes(text, new UnicodeEncoding(true, false), Utf16BeBom);

    internal static byte[] Windows1250(string text) => Bytes(text, Cp1250);

    /// <summary>
    /// A named fixture with what the parser should find in it.
    /// </summary>
    /// <param name="Name">A label for test output.</param>
    /// <param name="Data">The file bytes.</param>
    /// <param name="Texts">The expected cue texts.</param>
    /// <param name="Eol">The expected dominant line ending.</param>
    /// <param name="BomLength">The expected BOM length.</param>
    public sealed record Fixture(string Name, byte[] Data, string[] Texts, string Eol, int BomLength)
    {
        public override string ToString() => Name;
    }

    internal static IEnumerable<Fixture> All()
    {
        yield return new Fixture("utf8-lf", Utf8(Render(Cues(English))), English, "\n", 0);
        yield return new Fixture("utf8-crlf", Utf8(Render(Cues(English), "\r\n")), English, "\r\n", 0);
        yield return new Fixture("utf8-bom-crlf", Utf8(Render(Cues(German), "\r\n"), bom: true), German, "\r\n", 3);
        yield return new Fixture("utf8-bom-lf", Utf8(Render(Cues(German)), bom: true), German, "\n", 3);
        yield return new Fixture("utf8-no-bom-polish", Utf8(Render(Cues(Polish))), Polish, "\n", 0);
        yield return new Fixture("cp1250-crlf-polish", Windows1250(Render(Cues(Polish), "\r\n")), Polish, "\r\n", 0);
        yield return new Fixture("cp1250-lf-czech", Windows1250(Render(Cues(Czech))), Czech, "\n", 0);
        yield return new Fixture("utf16le-bom-crlf", Utf16Le(Render(Cues(German), "\r\n")), German, "\r\n", 2);
        yield return new Fixture("utf16be-bom-lf", Utf16Be(Render(Cues(German))), German, "\n", 2);
        yield return new Fixture("cr-only", Utf8(Render(Cues(English), "\r")), English, "\r", 0);
        yield return new Fixture("no-trailing-newline", Utf8(Render(Cues(English), tail: 0)), English, "\n", 0);
        yield return new Fixture("single-trailing-newline", Utf8(Render(Cues(English), tail: 1)), English, "\n", 0);
        yield return new Fixture("dot-milliseconds", Utf8(DotMilliseconds(Render(Cues(English)))), English, "\n", 0);
    }

    internal static string DotMilliseconds(string text) =>
        Regex.Replace(text, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2");

    /// <summary>
    /// A gap-less file with a cue missing its number line and stray spaces, like the test server's .es.srt.
    /// </summary>
    /// <returns>The raw bytes.</returns>
    internal static byte[] Malformed()
    {
        string text = Render(Cues(English), gap: false);
        text = text.Replace("3\n", string.Empty, StringComparison.Ordinal).Replace("--> ", "-->   ", StringComparison.Ordinal) + "  \n";
        return Utf8(text);
    }
}
