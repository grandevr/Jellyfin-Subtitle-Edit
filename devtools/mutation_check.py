#!/usr/bin/env python3
"""Mutation check: break the code in a handful of ways and confirm the test suite notices each one.

Mutants are built with analyzers off (the strict analyzers reject deliberately broken code); an unmodified control
copy is built the same way first and must pass.

Each mutant is a scratch copy of the project (never the real source), synced to
$SQE_TEST_ROOT/src/sqe-mut on the test host and tested in the SDK container. A mutant is "killed" when
`dotnet test` fails. Usage: python3 -I devtools/mutation_check.py
"""
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
TEST_ROOT = os.environ.get("SQE_TEST_ROOT") or sys.exit("set SQE_TEST_ROOT, see devtools/env.sh")
KEEP = ["src", "tests", "Directory.Build.props", ".editorconfig", "SubtitleQuickEdit.slnx"]

MUTANTS = [
    ("renumbering skipped",
     "src/SubtitleQuickEdit.Core/Srt/SrtEditor.cs",
     "if (adds.Count > 0 && original.Number != ordinal)", "if (false)"),
    ("new text ignores the file's line ending",
     "src/SubtitleQuickEdit.Core/Subtitles/ByteSplicer.cs",
     'string withBreaks = eol is null ? text : text.Replace("\\n", eol, StringComparison.Ordinal);',
     "string withBreaks = text;"),
    ("post-edit self-check ignores cue text",
     "src/SubtitleQuickEdit.Core/Srt/SrtEditor.cs",
     "|| !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal)", ""),
    ("encoder substitutes instead of throwing",
     "src/SubtitleQuickEdit.Core/Subtitles/SubtitleEncoding.cs",
     "strict.EncoderFallback = EncoderFallback.ExceptionFallback;",
     "strict.EncoderFallback = EncoderFallback.ReplacementFallback;"),
    ("always renames, never falls back to in-place",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "bool matched = original is null || metadata.TryMatch(temp, original.Value);", "bool matched = true;"),
    ("in-place file never deleted after verification",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "        File.Delete(inPlace);\n        SyncFolder(full, notes);\n    }\n\n    private void RecoverLocked", "        SyncFolder(full, notes);\n    }\n\n    private void RecoverLocked"),
    ("no re-check of the base hash before the rename",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "if (!string.Equals(recheck, expectedBaseHash, StringComparison.Ordinal))", "if (false)"),
    ("stale temp files are kept",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "            File.Delete(stale);\n", ""),
    ("interrupted in-place copy is not resumed",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "if (!string.Equals(targetHash, intendedHash, StringComparison.Ordinal))", "if (false)"),
    ("no folder sync after the rename",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "File.Move(temp, full, overwrite: true);\n                    SyncFolder(full, notes);", "File.Move(temp, full, overwrite: true);"),
    ("no folder sync of the .inplace marker rename",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "SyncFolder(full, notes); // the recovery file must be durable before the original is touched", ""),
    ("a failed folder sync fails the save",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "notes.Add(\"Could not fsync the folder '\" + dir + \"' (the file itself was written): \" + ex.Message);", "throw;"),
    ("temp file is not synced before the rename",
     "src/SubtitleQuickEdit.Core/Storage/SafeFileWriter.cs",
     "stream.Write(content);\n            durability.SyncFile(stream);", "stream.Write(content);"),
    ("platform selector always picks the Linux services",
     "src/SubtitleQuickEdit.Core/Storage/PlatformServices.cs",
     "        isLinux\n            ? new PlatformServices", "        true\n            ? new PlatformServices"),
    ("no backup is made before a save",
     "src/SubtitleQuickEdit.Core/Editing/SubtitleSaveService.cs",
     "backups.BeforeSave(request.ItemId, request.StreamIndex, request.Path, current);", ""),
    ("a new 'original' backup is made on every save",
     "src/SubtitleQuickEdit.Core/Storage/BackupStore.cs",
     'if (!Directory.EnumerateFiles(folder, key + "_*_" + OriginalKind + extension).Any())', "if (true)"),
    ("any file extension is taken for .srt",
     "src/SubtitleQuickEdit.Core/Subtitles/SubtitleFormats.cs",
     "        return null;\n", "        return SubtitleFormat.Srt;\n"),
    ("formats without a parser count as editable",
     "src/SubtitleQuickEdit.Core/Subtitles/SubtitleFormats.cs",
     "public static bool IsEditable(SubtitleFormat format) => format == SubtitleFormat.Srt;",
     "public static bool IsEditable(SubtitleFormat format) => true;"),
    ("backups always get the .srt extension",
     "src/SubtitleQuickEdit.Core/Storage/BackupStore.cs",
     "return string.IsNullOrEmpty(extension) ? \".srt\" : extension;", "return \".srt\";"),
    ("WebVTT text may contain an arrow",
     "src/SubtitleQuickEdit.Core/Vtt/VttEditor.cs",
     'if (line.Contains("-->", StringComparison.Ordinal))', "if (false)"),
    ("WebVTT self-check ignores cue text",
     "src/SubtitleQuickEdit.Core/Vtt/VttEditor.cs",
     "|| !string.Equals(cue.Text, expected[i].Text, StringComparison.Ordinal))", ")"),
    ("WebVTT payload swallows a following timing line",
     "src/SubtitleQuickEdit.Core/Vtt/VttParser.cs",
     "while (end < lines.Count && !IsBlank(decoded, lines[end]) && !IsTiming(decoded, lines[end], out _, out _))",
     "while (end < lines.Count && !IsBlank(decoded, lines[end]))"),
    ("WebVTT added cue not separated from the next block",
     "src/SubtitleQuickEdit.Core/Vtt/VttEditor.cs",
     "string inserted = (target.PrecededByBlankLine ? string.Empty : eol) + joined + eol + eol;",
     "string inserted = (target.PrecededByBlankLine ? string.Empty : eol) + joined + eol;"),
]


def run(cmd, env=None, capture=True):
    return subprocess.run(cmd, env=env, capture_output=capture, text=True, check=False)


def test_tree(work, flags, label):
    """Sync a tree to the mutant folder, force a rebuild, and run the tests.

    Returns ("build-failed" | "tests-failed" | "passed", failing test count, raw output)."""
    env = dict(os.environ, SQE_SRC=str(work), SQE_DIR=TEST_ROOT + "/src/sqe-mut", DOCKER_FLAGS=flags)
    reset = run([str(ROOT / "testdata/reset_fixtures.sh")], env)
    if reset.returncode != 0:
        print("reset_fixtures failed:", reset.stderr[-300:])
        sys.exit(2)
    prepare = run([str(ROOT / "devtools/prepare_mismatch.sh")], env)
    if prepare.returncode != 0:
        print("prepare_mismatch failed:", prepare.stderr[-300:])
        sys.exit(2)
    sync = run([str(ROOT / "devtools/sync.sh")], env)
    if sync.returncode != 0:
        print("sync failed:", sync.stderr[-300:])
        sys.exit(2)
    build = run([str(ROOT / "devtools/dotnet.sh"), "build", "-c", "Release", "--no-incremental",
                 "-p:TreatWarningsAsErrors=false", "-p:RunAnalyzers=false"], env)
    if build.returncode != 0:
        return "build-failed", 0, build.stdout + build.stderr
    result = run([str(ROOT / "devtools/dotnet.sh"), "test", "-c", "Release", "--no-build"], env)
    out = result.stdout + result.stderr
    if result.returncode == 0:
        return "passed", 0, out
    return "tests-failed", len(re.findall(r"^\s+Failed ", out, flags=re.M)), out


def copy_tree(tmp):
    work = pathlib.Path(tmp) / "m"
    work.mkdir()
    for item in KEEP:
        src = ROOT / item
        dst = work / item
        if src.is_dir():
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns("bin", "obj"))
        else:
            shutil.copy2(src, dst)
    return work


def main():
    flags = ("-v " + TEST_ROOT + "/src/mismatch:/mismatch -v " + TEST_ROOT + "/media:/fixtures:ro "
             "-e SQE_MISMATCH_FILE=/mismatch/file.srt -e SQE_FIXTURE_DIR=/fixtures -e SQE_REQUIRE_INTEGRATION=1")

    with tempfile.TemporaryDirectory() as tmp:
        status, _, out = test_tree(copy_tree(tmp), flags, "control")
    print("CONTROL (unmodified)  : " + status)
    if status != "passed":
        print(out[-1500:])
        sys.exit(2)

    killed = 0
    problems = []
    for name, rel, old, new in MUTANTS:
        with tempfile.TemporaryDirectory() as tmp:
            work = copy_tree(tmp)
            target = work / rel
            text = target.read_text()
            if old not in text:
                print("INVALID (pattern missing): " + name)
                problems.append(name + " [pattern missing]")
                continue
            target.write_text(text.replace(old, new, 1))
            status, failing, out = test_tree(work, flags, name)
        if status == "tests-failed":
            killed += 1
            print("KILLED (%2d tests fail): %s" % (failing, name))
        elif status == "build-failed":
            problems.append(name + " [mutant does not compile]")
            print("INVALID (does not compile): " + name)
            print("    " + "\n    ".join([l for l in out.splitlines() if " error " in l][:3]))
        else:
            problems.append(name + " [SURVIVED]")
            print("SURVIVED              : " + name)
    print("\n%d of %d mutants killed by failing tests" % (killed, len(MUTANTS)))
    if problems:
        print("problems: " + "; ".join(problems))
        sys.exit(1)


if __name__ == "__main__":
    main()
