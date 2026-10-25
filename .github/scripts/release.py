#!/usr/bin/env python3
"""Release helpers for the Release workflow (stdlib only).

  release.py info                         print version=, target_abi=, tag= lines (for $GITHUB_OUTPUT)
  release.py notes                        print the CHANGELOG.md section of the current version
  release.py package BUILD_DIR ZIP        zip the plugin files with a meta.json, print the zip's MD5
  release.py manifest URL MD5 TIMESTAMP   add the current version to manifest.json (newest first)

The version comes from Directory.Build.props, the target ABI from the Jellyfin.Controller package version in the
plugin's csproj (12.3.0 -> 12.3.0.0), and the changelog from the "## <version>" section of CHANGELOG.md.
"""
import hashlib
import json
import pathlib
import re
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
PROPS = ROOT / "Directory.Build.props"
CSPROJ = ROOT / "src" / "Jellyfin.Plugin.SubtitleQuickEdit" / "Jellyfin.Plugin.SubtitleQuickEdit.csproj"
CHANGELOG = ROOT / "CHANGELOG.md"
MANIFEST = ROOT / "manifest.json"
# What goes into the zip. Jellyfin's own assemblies and UtfUnknown come from the server and are not shipped.
PLUGIN_FILES = [
    "Jellyfin.Plugin.SubtitleQuickEdit.dll",
    "Jellyfin.Plugin.SubtitleQuickEdit.pdb",
    "SubtitleQuickEdit.Core.dll",
    "SubtitleQuickEdit.Core.pdb",
]


def fail(message):
    sys.exit("release.py: " + message)


def version():
    m = re.search(r"<Version>(\d+\.\d+\.\d+\.\d+)</Version>", PROPS.read_text(encoding="utf-8"))
    if not m:
        fail("no four-part <Version> in Directory.Build.props")
    return m.group(1)


def target_abi():
    m = re.search(r'Include="Jellyfin\.Controller" Version="(\d+\.\d+\.\d+)"', CSPROJ.read_text(encoding="utf-8"))
    if not m:
        fail("no Jellyfin.Controller version in " + CSPROJ.name)
    return m.group(1) + ".0"


def notes():
    text = CHANGELOG.read_text(encoding="utf-8")
    m = re.search(r"^## " + re.escape(version()) + r"[^\n]*\n(.*?)(?=^## |\Z)", text, re.M | re.S)
    if not m or not m.group(1).strip():
        fail("CHANGELOG.md has no section '## %s'" % version())
    return m.group(1).strip()


def manifest_entry():
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    if len(data) != 1:
        fail("manifest.json must hold exactly one plugin")
    return data, data[0]


def package(build_dir, zip_path):
    build = pathlib.Path(build_dir)
    _, plugin = manifest_entry()
    meta = {
        "category": plugin["category"],
        "changelog": notes(),
        "description": plugin["description"],
        "guid": plugin["guid"],
        "name": plugin["name"],
        "overview": plugin["overview"],
        "owner": plugin["owner"],
        "targetAbi": target_abi(),
        "version": version(),
    }
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for name in PLUGIN_FILES:
            path = build / name
            if not path.is_file():
                fail("missing build output " + str(path))
            z.write(path, name)
        z.writestr("meta.json", json.dumps(meta, indent=2) + "\n")
    return hashlib.md5(pathlib.Path(zip_path).read_bytes()).hexdigest()


def add_to_manifest(url, md5, timestamp):
    data, plugin = manifest_entry()
    v = version()
    if any(entry["version"] == v for entry in plugin["versions"]):
        fail("manifest.json already lists " + v)
    plugin["versions"].insert(0, {
        "version": v,
        "changelog": notes(),
        "targetAbi": target_abi(),
        "sourceUrl": url,
        "checksum": md5,
        "timestamp": timestamp,
    })
    MANIFEST.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main(argv):
    if argv[:1] == ["info"] and len(argv) == 1:
        print("version=" + version())
        print("target_abi=" + target_abi())
        print("tag=v" + version())
    elif argv[:1] == ["notes"] and len(argv) == 1:
        print(notes())
    elif argv[:1] == ["package"] and len(argv) == 3:
        print(package(argv[1], argv[2]))
    elif argv[:1] == ["manifest"] and len(argv) == 4:
        add_to_manifest(argv[1], argv[2], argv[3])
    else:
        fail("usage: see the docstring")


if __name__ == "__main__":
    main(sys.argv[1:])
