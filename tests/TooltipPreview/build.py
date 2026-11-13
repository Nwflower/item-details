#!/usr/bin/env python3
"""Build a TooltipPreview site from Item Details snapshot schema 1.

The output is a small HTML shell plus separate ``assets/``, ``data/`` and ``fonts/`` files.
The browser fetches no JSON (which fails for local ``file://`` pages): each data file is a tiny
script that registers already-serialized model data. The viewer renders only the typical items
first and pages the rest, so opening a 300-item snapshot does not build a thousand cards at once.

Only the Python standard library is used. Line breaks come from the snapshot (the game's own
RichTextBuilder.Build); the viewer never re-wraps text. Snapshot format: SNAPSHOT.md.
"""
from __future__ import annotations

import argparse
import difflib
import hashlib
import html
import json
import re
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

SCHEMA = 1
TOOL = Path(__file__).resolve().parent
PROJECT = TOOL.parents[1]
VIEWER = TOOL / "viewer"
DEFAULT_OUTPUT = PROJECT / "out" / "tooltip-preview" / "site"
DEFAULT_FONTS = PROJECT / "out" / "tooltip-preview" / "fonts"
DEFAULT_TYPICAL = TOOL / "typical.json"

FONT_FALLBACK_NOTE = "字体：用浏览器等宽字体代替游戏字体；字宽按 1 格（西文）/ 2 格（中日韩全角）近似，像素宽度不等于游戏。"
FONT_GAME_NOTE = ("字体：字形来自本机游戏资源里抽出的字体文件（tests/TooltipPreview/fonts.py），与游戏是同一款字体；"
                  "字宽仍按 1 格 / 2 格对齐，字距、行高和像素对齐是浏览器排版，不等于 TMP。")
APPROXIMATIONS = [
    "框宽：按快照的 frame.columns（缺省为 Build 的 recommendedWidth，再不行用 maxCharWidth）个字格画，不是游戏窗口的像素宽。",
    "<pos=…>：按百分比 / em / px 换算成行内游标位移（不是绝对定位块），列起点可能与游戏差几个像素。",
    "<size=…>：px 与百分比按 16px 基准换算；行高与游戏 TMP 不完全一致。",
    "sprite 进度条：按比例画色块，不是游戏贴图；没有记录比例时按格数还原。",
    "<sprite …> 图标与 <alpha>、<line-height>、<align> 等 TMP 标签只做近似或忽略（页脚列出本页遇到的未处理标签）。",
    "分类徽章、内容物格：只画文字与颜色，没有物品图标；位置是示意，不是游戏坐标。",
    "公司名并入：按 manufacturer.line 把公司名右对齐画到上一行，不复现 TMP 的 line-height=0 叠行。",
]
VARIANT_NOTES = [
    ("sectioned_tooltips-ticket",
     "Ticket 列只显示录制到的文字层：纸单、印章、图标和网格由 SectionedTooltips 在 Unity 里另外绘制，"
     "本页没有复刻；实际外观需要另用游戏内截图核对。"),
    ("sectioned_tooltips-classic",
     "Classic 列不包含对方另外绘制的侧边液位计与内容物面板；这里只比较文字，以及我方快照里记录到的部件。"),
]
STATE_NAMES = {"gene-scanner": "基因扫描仪开"}


# ---------------------------------------------------------------- snapshots


def load_documents(paths: Iterable[Path]) -> list[dict[str, Any]]:
    docs: list[dict[str, Any]] = []
    for path in paths:
        files = sorted(path.rglob("*.json")) if path.is_dir() else [path]
        for file in files:
            data = json.loads(file.read_text(encoding="utf-8"))
            if not isinstance(data, dict) or "items" not in data:
                continue
            if data.get("schema") != SCHEMA:
                print(f"warning: {file} schema={data.get('schema')!r}, expected {SCHEMA}", file=sys.stderr)
            data["_file"] = str(file)
            docs.append(data)
    return docs


def items_of(docs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [item for doc in docs for item in doc.get("items", [])]


def tag_signature(item: dict[str, Any]) -> str:
    canon = json.dumps(item.get("tags", []), sort_keys=True, ensure_ascii=False)
    return hashlib.sha1(canon.encode("utf-8")).hexdigest()[:10]


def item_key(item: dict[str, Any]) -> str:
    state = f"@{item['state']}" if item.get("state") else ""
    return f"{item.get('id', '')}#{tag_signature(item)}{state}"


def variant_of(doc: dict[str, Any]) -> str:
    return str(doc.get("meta", {}).get("variant") or Path(doc.get("_file", "")).parent.name or "?")


def plain_lines(item: dict[str, Any]) -> list[str]:
    return ["".join(s.get("text", "") for s in line.get("segments", [])) for line in item.get("lines", [])]


def segment_shape(item: dict[str, Any]) -> list[list[tuple]]:
    keys = ("text", "color", "bold", "italic", "underline", "strike", "background", "noBreak")
    shape = []
    for line in item.get("lines", []):
        row = []
        for s in line.get("segments", []):
            bar = s.get("bar") or {}
            row.append(tuple(s.get(k) for k in keys) + (bar.get("length"), bar.get("fill"), bar.get("ratio")))
        shape.append(row)
    return shape


# ---------------------------------------------------------------- typical items


def tag_map(item: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {t.get("k", ""): t for t in item.get("tags", [])}


def held_ids(item: dict[str, Any]) -> list[str]:
    out = []
    for inv in item.get("inventories") or []:
        if isinstance(inv, dict):
            out.extend(str(child.get("id", "")) for child in inv.get("items") or [] if isinstance(child, dict))
    return out


def rule_matches(rule: dict[str, Any], item: dict[str, Any]) -> bool:
    tags = tag_map(item)
    if "holds" in rule:
        if "type" in rule and rule["type"] not in item.get("types", []):
            return False
        return any(re.search(rule["holds"], child) for child in held_ids(item))
    if "tag" in rule:
        t = tags.get(rule["tag"])
        return bool(t and t.get("on"))
    if "tagPrefix" in rule:
        minimum = rule.get("min", 1)
        return any(k.startswith(rule["tagPrefix"]) and t.get("i", 0) >= minimum for k, t in tags.items())
    if "id" in rule:
        return item.get("id") == rule["id"]
    if "idPattern" in rule:
        return re.search(rule["idPattern"], item.get("id", "")) is not None
    if "type" in rule:
        return rule["type"] in item.get("types", []) or any(b.get("type") == rule["type"] for b in item.get("badges", []) or [])
    return False


def classify(items: list[dict[str, Any]], config: dict[str, Any]) -> tuple[list[tuple[dict, list[dict]]], list[dict]]:
    limit = int(config.get("perCategory", 3))
    picked: set[int] = set()
    groups = []
    for cat in config.get("categories", []):
        hits = [it for it in items if any(rule_matches(r, it) for r in cat.get("any", []))]
        chosen = hits[:limit]
        picked.update(id(it) for it in chosen)
        groups.append((cat, chosen))
    return groups, [it for it in items if id(it) not in picked]


def item_categories(item: dict[str, Any]) -> list[str]:
    """TypeBadge codes used by the left navigation; labels come from each badge's display text."""
    keys = []
    for badge in item.get("badges") or []:
        key = str(badge.get("type") or "")
        if key and key not in keys:
            keys.append(key)
    for key in item.get("types") or []:
        key = str(key or "")
        if key and key not in keys:
            keys.append(key)
    return keys


def category_labels(docs: list[dict[str, Any]]) -> dict[str, str]:
    labels: dict[str, str] = {}
    for item in items_of(docs):
        for key in item.get("types") or []:
            labels.setdefault(str(key), str(key))
        for badge in item.get("badges") or []:
            key = str(badge.get("type") or "")
            if key:
                labels[key] = str(badge.get("text") or key)
    return labels


def occurrence_keys(doc: dict[str, Any]) -> dict[tuple, dict[str, Any]]:
    seen: dict[tuple, int] = {}
    out: dict[tuple, dict[str, Any]] = {}
    for item in doc.get("items", []):
        base = (item.get("id", ""), item.get("state") or "")
        n = seen.get(base, 0)
        seen[base] = n + 1
        out[base + (n,)] = item
    return out


# ---------------------------------------------------------------- model


def columns_of(item: dict[str, Any]) -> int:
    frame = item.get("frame") or {}
    if frame.get("columns"):
        return int(frame["columns"])
    layers = item.get("layers") or {}
    if layers.get("recommendedWidth"):
        return min(int(layers["recommendedWidth"]), 60)
    return int(item.get("maxCharWidth") or 40)


def view_item(item: dict[str, Any], uid: str, raw: str) -> dict[str, Any]:
    """Keep what the viewer draws; calls/tags/tree/layers/window only for --raw full.

    ``window.text`` and ``layers`` repeat the same text as ``lines``; the default model keeps
    only the folded lines and a pre-computed column count.
    """
    out: dict[str, Any] = {"uid": uid, "state": item.get("state") or "", "columns": columns_of(item)}
    for key in ("id", "name", "source", "label", "maxCharWidth", "buildMaxCharWidth", "lines",
                "spriteBars", "manufacturer", "badges", "contentsGrid", "check"):
        if key in item and item[key] is not None:
            out[key] = item[key]
    calls = item.get("calls") or []
    if raw == "full":
        out["calls"] = calls
        out["tags"] = item.get("tags") or []
        out["types"] = item.get("types") or []
        out["inventories"] = item.get("inventories") or []
        out["raw"] = {k: v for k, v in item.items() if k not in ("calls", "tags", "inventories")}
    return out


def doc_summary(doc: dict[str, Any], label: str) -> str:
    meta = doc.get("meta", {})
    text = (f"{label}: source={meta.get('source')} game={meta.get('gameVersion')} lang={meta.get('language')} "
            f"mod={meta.get('modVersion')} exported={meta.get('exportedAt', '-')} items={len(doc.get('items', []))}")
    if meta.get("mods"):
        text += f" | 运行中的模组：{meta['mods']}"
    if meta.get("suspended"):
        text += f" | 录制时临时停用：{meta['suspended']}"
    if meta.get("geneScanner"):
        text += f" | 基因扫描仪：{meta['geneScanner']}"
    if meta.get("font"):
        text += f" | 游戏字体：{meta.get('font')}（{meta.get('fontFamily', '')}，{meta.get('fontSize', '?')}）"
    return text


def prepare_doc(doc: dict[str, Any], doc_id: str, raw: str, label_prefix: str = "") -> dict[str, Any]:
    label = (label_prefix + variant_of(doc)).strip(" ·")
    items = []
    orig = []
    by_orig: dict[int, str] = {}
    for n, item in enumerate(doc.get("items", [])):
        uid = f"{doc_id}:{n}"
        items.append(view_item(item, uid, raw))
        orig.append(item)
        by_orig[id(item)] = uid
    return {
        "id": doc_id,
        "label": label,
        "variant": variant_of(doc),
        "summary": doc_summary(doc, label),
        "meta": doc.get("meta", {}),
        "items": items,
        "orig": orig,
        "uids": [x["uid"] for x in items],
        "by_orig": by_orig,
    }


def public_doc(pack: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": pack["id"], "label": pack["label"], "variant": pack["variant"],
        "summary": pack["summary"], "meta": pack["meta"],
        "files": {"typical": f"data/{pack['id']}-typical.js", "rest": f"data/{pack['id']}-rest.js"},
        "counts": {"all": len(pack["items"]), "typical": 0, "rest": len(pack["items"])},
    }


def rows_for_single(packs: list[dict[str, Any]], typical: dict[str, Any], only_typical: bool):
    items_storage = []
    rows_by_orig: dict[int, dict[str, Any]] = {}
    for pack in packs:
        for item, uid in zip(pack["orig"], pack["uids"]):
            row = {"key": uid, "id": item.get("id", ""), "name": item.get("name", ""),
                   "state": item.get("state") or "", "categories": item_categories(item),
                   "cells": {pack["id"]: uid}}
            rows_by_orig[id(item)] = row
            items_storage.append((item, row))
    groups, rest = classify([i for i, _ in items_storage], typical)
    return _section_data(groups, rest, rows_by_orig, only_typical)


def rows_for_compare(packs: list[dict[str, Any]], typical: dict[str, Any], only_typical: bool):
    columns = [(pack, occurrence_keys({"items": pack["orig"]})) for pack in packs]
    order: list[tuple] = []
    for _pack, by_key in columns:
        for key in by_key:
            if key not in order:
                order.append(key)
    first: dict[tuple, dict[str, Any]] = {}
    rows_by_orig: dict[int, dict[str, Any]] = {}
    for key in order:
        present = [by[key] for _pack, by in columns if key in by]
        item = present[0]
        categories = []
        for value in present:
            for category in item_categories(value):
                if category not in categories:
                    categories.append(category)
        row = {
            "key": "#".join(str(x) for x in key),
            "id": item.get("id", ""),
            "name": item.get("name", ""),
            "state": item.get("state") or "",
            "categories": categories,
            "same": len(present) == len(packs) and all(plain_lines(i) == plain_lines(present[0]) for i in present),
            "cells": {pack["id"]: pack["by_orig"].get(id(by[key])) if key in by else None for pack, by in columns},
        }
        first[key] = item
        for value in present:
            rows_by_orig[id(value)] = row
    groups, rest = classify([first[k] for k in order], typical)
    return _section_data(groups, rest, rows_by_orig, only_typical)


def _section_data(groups, rest, rows_by_orig, only_typical):
    out_groups = []
    for cat, chosen in groups:
        out_groups.append({
            "key": cat.get("key", ""),
            "title": cat.get("title", cat.get("key", "")),
            "rows": [rows_by_orig[id(item)] for item in chosen],
        })
    rest_rows = [] if only_typical else [rows_by_orig[id(item)] for item in rest]
    return out_groups, rest_rows


def diff_rows(old_docs: list[dict[str, Any]], new_docs: list[dict[str, Any]], uid_of: dict[int, str]) -> dict[str, Any]:
    changed, removed, added = diff_items(items_of(old_docs), items_of(new_docs))
    rows = []
    for a, b, kind in changed:
        udiff = "\n".join(difflib.unified_diff(plain_lines(a), plain_lines(b), "旧", "新", lineterm=""))
        rows.append({
            "id": b.get("id", ""),
            "name": b.get("name", ""),
            "kind": kind,
            "udiff": udiff or "（文字相同，片段颜色或样式不同）",
            "oldUid": uid_of.get(id(a)),
            "newUid": uid_of.get(id(b)),
        })
    return {
        "changed": rows,
        "removed": [i.get("id", "") for i in removed],
        "added": [i.get("id", "") for i in added],
        "changedCount": len(rows),
        "removedCount": len(removed),
        "addedCount": len(added),
    }


def diff_items(old: list[dict[str, Any]], new: list[dict[str, Any]]):
    old_by = {item_key(i): i for i in old}
    new_by = {item_key(i): i for i in new}
    pairs = [(old_by[k], new_by[k]) for k in new_by if k in old_by]
    lone_old = [i for k, i in old_by.items() if k not in new_by]
    lone_new = [i for k, i in new_by.items() if k not in old_by]
    for item in list(lone_new):
        same = [o for o in lone_old if o.get("id") == item.get("id")]
        mates = [n for n in lone_new if n.get("id") == item.get("id")]
        if len(same) == 1 and len(mates) == 1:
            pairs.append((same[0], item))
            lone_old.remove(same[0])
            lone_new.remove(item)
    changed = []
    for a, b in pairs:
        text_changed = plain_lines(a) != plain_lines(b)
        if text_changed or segment_shape(a) != segment_shape(b):
            changed.append((a, b, "文字" if text_changed else "片段样式"))
    return changed, lone_old, lone_new


# ---------------------------------------------------------------- fonts


def load_fonts(font_dir: Path | None) -> list[dict[str, Any]]:
    font_dir = font_dir or DEFAULT_FONTS
    index = font_dir / "fonts.json"
    if not index.is_file():
        return []
    fonts = json.loads(index.read_text(encoding="utf-8"))
    for n, font in enumerate(fonts):
        font["css"] = f"gamefont{n}"
        font["path"] = font_dir / font["file"]
    return fonts


def font_stack(docs: list[dict[str, Any]], fonts: list[dict[str, Any]], forced: list[str]) -> list[dict[str, Any]]:
    wanted = list(forced)
    for doc in docs:
        meta = doc.get("meta", {})
        raws = [meta.get("fontFamily", "")] + [e.split("=", 1)[-1] for e in str(meta.get("fontFallbacks", "")).split(";")]
        for raw in raws:
            family = raw.split("|")[0].strip()
            if family and family not in wanted:
                wanted.append(family)
    stack = []
    for family in wanted:
        low = family.lower()
        hit = next((f for f in fonts if low in (f.get("family", "").lower(), f.get("fullName", "").lower())), None) \
            or next((f for f in fonts if low in f.get("fullName", "").lower() or f.get("family", "").lower() in low), None)
        if hit and hit not in stack:
            stack.append(hit)
    return stack


def font_css(stack: list[dict[str, Any]]) -> str:
    faces = "".join(f"@font-face{{font-family:'{f['css']}';src:url('../fonts/{f['file']}');font-display:block}}" for f in stack)
    families = ",".join([f"'{f['css']}'" for f in stack] + ["Consolas", "'Cascadia Mono'", "'Noto Sans Mono CJK SC'", "monospace"])
    return f"{faces}:root{{--tipfont:{families}}}\n"


# ---------------------------------------------------------------- site


def build_payload(docs: list[dict[str, Any]], old_docs: list[dict[str, Any]] | None, typical: dict[str, Any],
                  title: str, raw: str, compare: bool, only_typical: bool, forced_fonts: list[str],
                  fonts: list[dict[str, Any]]) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    packs = [prepare_doc(doc, f"doc-{i}", raw) for i, doc in enumerate(docs)]
    if not packs:
        raise ValueError("no snapshot documents")

    diff = None
    site_docs = packs
    if old_docs:
        old_packs = [prepare_doc(doc, f"old-{i}", raw, "旧 · ") for i, doc in enumerate(old_docs)]
        new_packs = [prepare_doc(doc, f"new-{i}", raw, "新 · ") for i, doc in enumerate(packs)]
        uid_of = {id(item): uid for pack in old_packs + new_packs for item, uid in zip(pack["orig"], pack["uids"])}
        diff = diff_rows(old_docs, docs, uid_of)
        groups, rest = rows_for_single(new_packs, typical, only_typical)
        site_docs = old_packs + new_packs
        mode = "diff"
    elif compare:
        groups, rest = rows_for_compare(packs, typical, only_typical)
        mode = "compare"
    else:
        groups, rest = rows_for_single(packs, typical, only_typical)
        mode = "single"

    combined_variants = " ".join(p["variant"] for p in site_docs)
    notes = [text for needle, text in VARIANT_NOTES if needle in combined_variants]
    stack = font_stack(docs, fonts, forced_fonts)
    font_note = (FONT_GAME_NOTE + " 本页用到：" + "、".join(f["fullName"] for f in stack)) if stack else FONT_FALLBACK_NOTE
    typical_uids = {
        uid
        for group in groups
        for row in group.get("rows", [])
        for uid in (row.get("cells") or {}).values()
        if uid
    }
    by_pack = {p["id"]: p for p in site_docs}
    docs_public = [public_doc(p) for p in site_docs]
    for doc in docs_public:
        pack = by_pack.get(doc["id"])
        if pack is not None:
            typical_count = sum(1 for uid in pack["uids"] if uid in typical_uids)
            if only_typical:
                doc["counts"] = {"all": len(pack["items"]), "typical": len(pack["items"]), "rest": 0}
            else:
                doc["counts"] = {"all": len(pack["items"]), "typical": typical_count,
                                  "rest": len(pack["items"]) - typical_count}

    labels = category_labels(docs)
    counts: dict[str, int] = {}
    for group in groups:
        for row in group.get("rows", []):
            for key in row.get("categories", []):
                counts[key] = counts.get(key, 0) + 1
    for row in rest:
        for key in row.get("categories", []):
            counts[key] = counts.get(key, 0) + 1
    categories = [
        {"key": key, "title": labels.get(key, key), "count": count}
        for key, count in sorted(
            counts.items(), key=lambda pair: (-pair[1], labels.get(pair[0], pair[0]), pair[0]))
    ]

    site = {
        "schema": SCHEMA,
        "title": title,
        "mode": mode,
        "raw": raw,
        "onlyTypical": only_typical,
        "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "docs": docs_public,
        "groups": groups,
        "rest": rest,
        "categories": categories,
        "diff": diff,
        "notes": notes,
        "approximations": [font_note] + APPROXIMATIONS,
        "fontNames": [f["fullName"] for f in stack],
        "stateNames": STATE_NAMES,
    }
    return site, stack


def write_module(path: Path, name: str, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    path.write_text(
        "window.TooltipPreviewData=window.TooltipPreviewData||{};\n"
        f"window.TooltipPreviewData.{name}={payload};\n",
        encoding="utf-8", newline="\n")


def write_doc_module(path: Path, doc_id: str, items: list[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps({"id": doc_id, "items": items}, ensure_ascii=False, separators=(",", ":"))
    path.write_text(
        'window.TooltipPreviewData=window.TooltipPreviewData||{};\n'
        'window.TooltipPreviewData.docsData=window.TooltipPreviewData.docsData||{};\n'
        f'(function(){{var d=window.TooltipPreviewData.docsData[{json.dumps(doc_id)}]||{{id:{json.dumps(doc_id)},items:[]}};'
        f'd.items=d.items.concat({payload}.items);window.TooltipPreviewData.docsData[{json.dumps(doc_id)}]=d;}})();\n',
        encoding="utf-8", newline="\n")


SITE_MARKER = ".tooltip-preview-site"


def prepare_output(output: Path) -> None:
    """Only regenerate a directory this tool owns; never wipe an arbitrary non-empty folder."""
    if output.exists():
        entries = list(output.iterdir())
        if entries and not (output / SITE_MARKER).is_file():
            raise ValueError(f"{output} 不是 TooltipPreview 生成的目录；请换一个空目录或删掉旧产物。")
        for name in ("assets", "data", "fonts"):
            target = output / name
            if target.exists():
                shutil.rmtree(target)
        index = output / "index.html"
        if index.exists():
            index.unlink()
    output.mkdir(parents=True, exist_ok=True)
    (output / SITE_MARKER).write_text("TooltipPreview\n", encoding="utf-8", newline="\n")
    (output / "assets").mkdir(exist_ok=True)
    (output / "data").mkdir(exist_ok=True)
    (output / "fonts").mkdir(exist_ok=True)


def write_site(output: Path, site: dict[str, Any], packs: list[dict[str, Any]], stack: list[dict[str, Any]]) -> None:
    prepare_output(output)
    shutil.copyfile(VIEWER / "app.js", output / "assets" / "app.js")
    shutil.copyfile(VIEWER / "style.css", output / "assets" / "style.css")
    template = (VIEWER / "index.html").read_text(encoding="utf-8")
    (output / "index.html").write_text(template.replace("{{TITLE}}", html.escape(site["title"])),
                                       encoding="utf-8", newline="\n")
    write_module(output / "data" / "index.js", "site", site)
    typical_uids = {
        uid
        for group in site.get("groups", [])
        for row in group.get("rows", [])
        for uid in (row.get("cells") or {}).values()
        if uid
    }
    for pack in packs:
        typical_items = [item for item in pack["items"] if item["uid"] in typical_uids]
        rest_items = [item for item in pack["items"] if item["uid"] not in typical_uids]
        write_doc_module(output / "data" / f"{pack['id']}-typical.js", pack["id"], typical_items)
        if not site.get("onlyTypical"):
            write_doc_module(output / "data" / f"{pack['id']}-rest.js", pack["id"], rest_items)
    for font in stack:
        shutil.copyfile(font["path"], output / "fonts" / font["file"])
    (output / "assets" / "fonts.css").write_text(font_css(stack), encoding="utf-8", newline="\n")


def build_site(docs: list[dict[str, Any]], old_docs: list[dict[str, Any]] | None, typical: dict[str, Any],
               title: str, raw: str, compare: bool, only_typical: bool, forced_fonts: list[str],
               fonts: list[dict[str, Any]], output: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    site, stack = build_payload(docs, old_docs, typical, title, raw, compare, only_typical, forced_fonts, fonts)
    packs = [prepare_doc(doc, f"doc-{i}", raw) for i, doc in enumerate(docs)]
    if old_docs:
        packs = ([prepare_doc(doc, f"old-{i}", raw, "旧 · ") for i, doc in enumerate(old_docs)] +
                 [prepare_doc(doc, f"new-{i}", raw, "新 · ") for i, doc in enumerate(docs)])
    write_site(output, site, packs, stack)
    return site, stack


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inputs", nargs="+", type=Path, help="snapshot JSON files or directories")
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT, help="site directory (not an .html file)")
    parser.add_argument("--typical", type=Path, default=DEFAULT_TYPICAL, help="typical-item rules (JSON)")
    parser.add_argument("--diff", type=Path, help="older snapshot file or directory to compare against")
    parser.add_argument("--compare", action="store_true",
                        help="one column per input snapshot (variants of the same save), aligned by order")
    parser.add_argument("--only-typical", action="store_true", help="build only the typical section")
    parser.add_argument("--raw", choices=("none", "full"), default="none",
                        help="per-item raw data: none (default, no debug block on the page); full = calls/tags/tree")
    parser.add_argument("--fonts", type=Path, help="folder written by fonts.py (default: out/tooltip-preview/fonts)")
    parser.add_argument("--font", action="append", default=[], metavar="FAMILY",
                        help="force a font family to the front of the stack (repeatable)")
    parser.add_argument("--title", default="Item Details · tooltip 预览")
    args = parser.parse_args(argv)

    docs = load_documents(args.inputs)
    if not docs:
        print("no snapshot documents found", file=sys.stderr)
        return 2
    old_docs = load_documents([args.diff]) if args.diff else None
    typical = json.loads(args.typical.read_text(encoding="utf-8"))
    fonts = load_fonts(args.fonts)
    try:
        site, stack = build_site(docs, old_docs, typical, args.title, args.raw, args.compare,
                                 args.only_typical, args.font, fonts, args.output)
    except (OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    if site.get("diff"):
        diff = site["diff"]
        print(f"diff: changed={diff['changedCount']} removed={diff['removedCount']} added={diff['addedCount']}")
    data_dir = args.output / "data"
    initial_bytes = (data_dir / "index.js").stat().st_size + sum(
        (args.output / doc["files"]["typical"]).stat().st_size for doc in site["docs"]
        if (args.output / doc["files"]["typical"]).is_file())
    data_bytes = sum(p.stat().st_size for p in data_dir.glob("*.js"))
    html_size = (args.output / "index.html").stat().st_size
    print(f"wrote {args.output / 'index.html'} ({sum(len(d.get('items', [])) for d in docs)} items, "
          f"html {html_size} B, initial data {initial_bytes / 1024:.1f} KB, all data {data_bytes / 1024 / 1024:.2f} MB, "
          f"fonts: {', '.join(site['fontNames']) if stack else 'browser monospace'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
