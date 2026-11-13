#!/usr/bin/env python3
"""Copy the game's own font files out of its Unity asset files, for TooltipPreview/build.py.

The game ships its fonts inside ``sharedassets*.assets`` / ``resources.assets`` (no loose .ttf).
Unity stores a Font's source file as a plain byte array, so the TrueType/OpenType table directory
is intact and the file can be located by its header and cut out by the extent of its tables.
Only the Python standard library is used; nothing is decompressed or parsed beyond the sfnt header.

    python Modding/ItemDetails/tests/TooltipPreview/fonts.py --game-dir "<game>" [-o out/tooltip-preview/fonts]

The output folder gets one .ttf/.otf per font plus fonts.json (file, family, fullName, bytes).
These are the game's licensed assets: keep them in the ignored ``out/`` tree, never commit or ship them.
"""
from __future__ import annotations

import argparse
import json
import mmap
import re
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = REPO / "out" / "tooltip-preview" / "fonts"
SIGNATURES = (b"\x00\x01\x00\x00", b"OTTO", b"true")
REQUIRED = (b"cmap", b"head", b"name")


def read_font(buf, base: int):
    """Return (names, length) when a plausible sfnt starts at ``base``, else None."""
    try:
        count = struct.unpack_from(">H", buf, base + 4)[0]
        if not 8 <= count <= 40:
            return None
        tables = {}
        end = 0
        for i in range(count):
            tag, _checksum, offset, length = struct.unpack_from(">4sIII", buf, base + 12 + 16 * i)
            if not all(32 <= c < 127 for c in tag):
                return None
            tables[tag] = (offset, length)
            end = max(end, offset + length)
        if any(tag not in tables for tag in REQUIRED) or base + end > len(buf):
            return None
        offset, _length = tables[b"name"]
        start = base + offset
        records, strings = struct.unpack_from(">HH", buf, start + 2)
        names: dict[int, str] = {}
        for i in range(records):
            platform, _enc, _lang, name_id, length, at = struct.unpack_from(">6H", buf, start + 6 + 12 * i)
            if name_id not in (1, 2, 4):
                continue
            raw = bytes(buf[start + strings + at:start + strings + at + length])
            try:
                text = raw.decode("utf-16-be") if platform in (0, 3) else raw.decode("latin-1")
            except UnicodeDecodeError:
                continue
            # 同一个 nameID 有多条时优先留纯 ASCII 的那条（CSS 与游戏里的 familyName 都用它）。
            if name_id not in names or (text.isascii() and not names[name_id].isascii()):
                names[name_id] = text
        if 1 not in names and 4 not in names:
            return None
        return names, end
    except (struct.error, IndexError):
        return None


def carve(asset: Path):
    with asset.open("rb") as handle:
        buf = mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            seen: set[int] = set()
            for signature in SIGNATURES:
                at = 0
                while True:
                    at = buf.find(signature, at)
                    if at < 0:
                        break
                    found = read_font(buf, at) if at not in seen else None
                    if found:
                        names, length = found
                        seen.add(at)
                        yield names, bytes(buf[at:at + length]), signature
                    at += 4
        finally:
            buf.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args(argv)

    data = args.game_dir / "Probably Stolen_Data"
    assets = sorted(data.glob("*.assets"))
    if not assets:
        print(f"no .assets files under {data}", file=sys.stderr)
        return 2
    args.output.mkdir(parents=True, exist_ok=True)
    entries = []
    used: set[str] = set()
    for asset in assets:
        for names, blob, signature in carve(asset):
            family = names.get(1) or names.get(4) or "font"
            full = names.get(4) or family
            stem = re.sub(r"[^A-Za-z0-9]+", "_", full).strip("_") or "font"
            name = stem
            serial = 2
            while name.lower() in used:
                name = f"{stem}_{serial}"
                serial += 1
            used.add(name.lower())
            file = name + (".otf" if signature == b"OTTO" else ".ttf")
            (args.output / file).write_bytes(blob)
            entries.append({"file": file, "family": family, "style": names.get(2, ""), "fullName": full,
                            "bytes": len(blob), "from": asset.name})
            print(f"{file}: {full} ({len(blob)} bytes, {asset.name})")
    if not entries:
        print("no fonts found: the game may have moved its fonts into compressed bundles", file=sys.stderr)
        return 1
    (args.output / "fonts.json").write_text(json.dumps(entries, ensure_ascii=False, indent=1) + "\n",
                                            encoding="utf-8", newline="\n")
    print(f"wrote {len(entries)} font(s) to {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
