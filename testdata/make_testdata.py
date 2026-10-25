#!/usr/bin/env python3
"""Generate the throwaway SRT test library for the Subtitle Editor plugin.

Usage: make_testdata.py <output-dir>
Creates <output-dir>/TestShow/Season 01/ with .srt files for two episodes in
the encodings and line endings listed in the spec. The videos are generated
separately (inside the test Jellyfin container).
"""
import os
import sys

PL = [
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
]
EN = [
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
]
DE = [
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
]
CS = [
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
]


def ts(ms):
    h, rem = divmod(ms, 3600000)
    m, rem = divmod(rem, 60000)
    s, milli = divmod(rem, 1000)
    return "%02d:%02d:%02d,%03d" % (h, m, s, milli)


def cues(texts, step=4500, dur=3000, start=2000):
    return [(start + i * step, start + i * step + dur, t) for i, t in enumerate(texts)]


def render(cs, eol="\n", number=True, gap=True, trailing_newline=True):
    out = []
    for i, (a, b, t) in enumerate(cs, 1):
        block = ""
        if number:
            block += str(i) + eol
        block += ts(a) + " --> " + ts(b) + eol
        block += t.replace("\n", eol)
        out.append(block)
    sep = eol + eol if gap else eol
    text = sep.join(out)
    if trailing_newline:
        text += eol + eol
    return text


def write(path, text, encoding, bom=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = text.encode(encoding)
    if bom:
        data = bom + data
    with open(path, "wb") as f:
        f.write(data)


def main(root):
    d = os.path.join(root, "TestShow", "Season 01")
    e1 = "TestShow - S01E01"
    e2 = "TestShow - S01E02"
    c_en, c_pl, c_de, c_cs = cues(EN), cues(PL), cues(DE), cues(CS)

    # Episode 1
    write(os.path.join(d, e1 + ".en.srt"), render(c_en, "\n"), "utf-8")                       # UTF-8, LF
    write(os.path.join(d, e1 + ".pl.srt"), render(c_pl, "\r\n"), "cp1250")                    # Windows-1250, CRLF
    write(os.path.join(d, e1 + ".de.srt"), render(c_de, "\r\n"), "utf-8", bom=b"\xef\xbb\xbf")  # UTF-8 BOM, CRLF
    write(os.path.join(d, e1 + ".fr.srt"), render(c_de, "\r\n"), "utf-16-le", bom=b"\xff\xfe")  # UTF-16LE BOM, CRLF
    # Malformed: no blank lines between cues, stray spaces, one cue without a number line
    bad = render(c_en, "\n", gap=False)
    bad = bad.replace("3\n", "", 1).replace("--> ", "-->   ", 2) + "  \n"
    write(os.path.join(d, e1 + ".es.srt"), bad, "utf-8")

    # Episode 2
    write(os.path.join(d, e2 + ".en.srt"), render(c_en, "\r\n"), "utf-8")                     # UTF-8, CRLF
    write(os.path.join(d, e2 + ".pl.srt"), render(c_pl, "\n"), "utf-8")                       # UTF-8, LF
    write(os.path.join(d, e2 + ".cs.srt"), render(c_cs, "\n"), "cp1250")                      # Windows-1250, LF
    write(os.path.join(d, e2 + ".de.srt"), render(c_de, "\n"), "utf-8", bom=b"\xef\xbb\xbf")  # UTF-8 BOM, LF
    # No trailing newline at end of file
    write(os.path.join(d, e2 + ".it.srt"), render(c_en, "\n", trailing_newline=False), "utf-8")


if __name__ == "__main__":
    main(sys.argv[1])
