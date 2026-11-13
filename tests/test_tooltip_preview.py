"""Regressions for the formal TooltipPreview site builder; standard library only.

The viewer itself is JavaScript, so these tests name the build invariants that make the page
fast and usable: a tiny HTML shell, external data chunks, lazy rest loading, no shrinking cards,
and the formal output-directory guard.
"""
from __future__ import annotations

import contextlib
import copy
import io
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest

TOOL = Path(__file__).resolve().parent.parent / "tests" / "TooltipPreview"
sys.path.insert(0, str(TOOL))
import build as preview  # noqa: E402

SAMPLE = Path(__file__).resolve().parent / "fixtures" / "tooltip-preview" / "sample.json"
OUT = Path(__file__).resolve().parent.parent / "out" / "tooltip-preview-tests"


def read_site(site_dir: Path) -> dict:
    text = (site_dir / "data" / "index.js").read_text(encoding="utf-8")
    marker = "window.TooltipPreviewData.site="
    start = text.index(marker) + len(marker)
    return json.loads(text[start:].rstrip().removesuffix(";"))


def read_doc(site_dir: Path, doc_id: str, kind: str) -> dict:
    text = (site_dir / "data" / f"{doc_id}-{kind}.js").read_text(encoding="utf-8")
    start = text.index("concat(") + len("concat(")
    end = text.index("}.items);", start) + 1
    value = json.loads(text[start:end])
    assert value["id"] == doc_id
    return value


def run_build(root: Path, *argv: str) -> tuple[int, Path]:
    site_dir = root / "site"
    with contextlib.redirect_stdout(io.StringIO()):
        code = preview.main([*argv, "-o", str(site_dir)])
    return code, site_dir


class TooltipPreviewSiteTests(unittest.TestCase):
    def setUp(self) -> None:
        OUT.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=OUT)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_site_is_a_small_shell_with_external_chunks(self) -> None:
        code, site_dir = run_build(self.root, str(SAMPLE))
        self.assertEqual(code, 0)
        page = (site_dir / "index.html").read_text(encoding="utf-8")
        self.assertLess(len(page.encode("utf-8")), 2000)
        self.assertIn("data/index.js", page)
        self.assertIn("assets/app.js", page)
        self.assertNotIn("sample_bottle", page)
        self.assertNotIn('<div class="tip"', page)
        site = read_site(site_dir)
        self.assertEqual(site["mode"], "single")
        self.assertEqual(len(site["docs"]), 1)
        self.assertEqual([(c["key"], c["title"], c["count"]) for c in site["categories"]],
                         [("WATER", "水", 1)])
        bottle_row = next(row for group in site["groups"] for row in group["rows"] if row["id"] == "sample_bottle")
        self.assertEqual(bottle_row["categories"], ["WATER"])
        doc = site["docs"][0]
        self.assertEqual(doc["counts"]["all"], 3)
        self.assertEqual(doc["counts"]["typical"] + doc["counts"]["rest"], 3)
        self.assertTrue((site_dir / "data" / "doc-0-typical.js").is_file())
        self.assertTrue((site_dir / "data" / "doc-0-rest.js").is_file())
        self.assertTrue((site_dir / preview.SITE_MARKER).is_file())
        typical = read_doc(site_dir, "doc-0", "typical")
        rest = read_doc(site_dir, "doc-0", "rest")
        self.assertEqual(len(typical["items"]) + len(rest["items"]), 3)
        self.assertEqual(site["raw"], "none")
        self.assertTrue(all("callsSummary" not in item and "calls" not in item for item in typical["items"] + rest["items"]))

    def test_typical_section_and_rest_are_separate(self) -> None:
        code, site_dir = run_build(self.root, str(SAMPLE))
        self.assertEqual(code, 0)
        site = read_site(site_dir)
        titles = [group["title"] for group in site["groups"]]
        self.assertIn("含水容器", titles)
        self.assertIn("鉴定品", titles)
        self.assertEqual(site["rest"][0]["id"], "sample_plain")
        # 典型块里只放典型物品；其余物品在 rest 块，首屏不解析。
        typical = read_doc(site_dir, "doc-0", "typical")
        self.assertEqual({item["id"] for item in typical["items"]}, {"sample_bottle", "sample_hand"})
        rest = read_doc(site_dir, "doc-0", "rest")
        self.assertEqual([item["id"] for item in rest["items"]], ["sample_plain"])

    def test_cards_keep_their_width_by_default(self) -> None:
        css = (TOOL / "viewer" / "style.css").read_text(encoding="utf-8")
        self.assertRegex(css, r"\.card\{[^}]*flex:0 0 auto")
        self.assertRegex(css, r"\.cols\{[^}]*flex-wrap:nowrap")
        self.assertIn(".cols>.card{flex:0 0 auto}", css)
        self.assertRegex(css, r"\.card\{[^}]*max-width:none")
        self.assertRegex(css, r"\.tip\{[^}]*max-width:none")
        self.assertRegex(css, r"\.tip\{[^}]*overflow:visible")
        # 单列换行，对比列横向滚动；两种模式都不允许压窄卡片，也不要用视口上限裁掉右对齐文本。
        self.assertIn(".cards{display:flex;flex-wrap:wrap", css)
        self.assertNotIn("max-width:calc(100vw", css)

    def test_viewer_loads_chunks_on_demand(self) -> None:
        app = (TOOL / "viewer" / "app.js").read_text(encoding="utf-8")
        for token in ("loadChunk", "ensureRest", "files[kind]", "state.restShown", "state.pageSize",
                      "state.category", "分类标签", "#cats", "lineExtent", "posUnits", "pos-gap"):
            self.assertIn(token, app)
        self.assertIn("正在加载其余数据", app)
        self.assertIn("loading", app)

    def test_holds_rule_and_columns(self) -> None:
        machine = {"id": "m", "types": ["MACHINE"], "inventories": [{"kind": "x", "items": [{"id": "energy_credit_ext"}]}]}
        rule = {"type": "MACHINE", "holds": "^energy_credit"}
        self.assertTrue(preview.rule_matches(rule, machine))
        self.assertFalse(preview.rule_matches(rule, {"id": "m", "types": ["TOOL"], "inventories": machine["inventories"]}))
        source = json.loads(SAMPLE.read_text(encoding="utf-8"))
        hand = next(i for i in source["items"] if i["id"] == "sample_hand")
        self.assertEqual(preview.columns_of(hand), 40)
        self.assertEqual(preview.columns_of({"frame": {"columns": 12}}), 12)

    def test_compare_rows_and_variant_notes(self) -> None:
        base = json.loads(SAMPLE.read_text(encoding="utf-8"))
        base["meta"]["variant"] = "item_details"
        other = copy.deepcopy(base)
        other["meta"]["variant"] = "item_details+sectioned_tooltips-ticket"
        other["meta"]["mods"] = "item_details@1; sectioned_tooltips@1.7.0"
        items = {i["id"]: i for i in other["items"]}
        items["sample_bottle"]["lines"][1]["segments"][1]["text"] = "容量 "
        other["items"] = [items["sample_bottle"], items["sample_hand"]]
        paths = []
        for name, doc in (("a", base), ("b", other)):
            path = self.root / f"{name}.json"
            path.write_text(json.dumps(doc, ensure_ascii=False), encoding="utf-8")
            paths.append(str(path))
        with contextlib.redirect_stdout(io.StringIO()):
            code = preview.main(["--compare", *paths, "-o", str(self.root / "site")])
        self.assertEqual(code, 0)
        site = read_site(self.root / "site")
        self.assertEqual(site["mode"], "compare")
        self.assertEqual([d["label"] for d in site["docs"]], ["item_details", "item_details+sectioned_tooltips-ticket"])
        bottle = next(row for group in site["groups"] for row in group["rows"] if row["id"] == "sample_bottle")
        self.assertEqual(len(bottle["cells"]), 2)
        self.assertIs(bottle["same"], False)
        hand = next(row for group in site["groups"] for row in group["rows"] if row["id"] == "sample_hand")
        self.assertIs(hand["same"], True)
        self.assertTrue(any("Ticket 列只显示" in note for note in site["notes"]))

    def test_diff_payload_keeps_text_changes(self) -> None:
        new = copy.deepcopy(json.loads(SAMPLE.read_text(encoding="utf-8")))
        new["items"][0]["lines"][1]["segments"][1]["text"] = "容量 "
        new_path = self.root / "new.json"
        new_path.write_text(json.dumps(new, ensure_ascii=False), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = preview.main([str(new_path), "--diff", str(SAMPLE), "-o", str(self.root / "site")])
        self.assertEqual(code, 0)
        self.assertIn("changed=1", output.getvalue())
        site = read_site(self.root / "site")
        self.assertEqual(site["mode"], "diff")
        self.assertEqual(site["diff"]["changedCount"], 1)
        self.assertIn("旧", site["diff"]["changed"][0]["udiff"])
        self.assertIn("容量", site["diff"]["changed"][0]["udiff"])
        old = read_doc(self.root / "site", "old-0", "rest")
        newdoc = read_doc(self.root / "site", "new-0", "rest")
        self.assertTrue(old["items"] and newdoc["items"])

    def test_fonts_are_copied_and_css_is_relative(self) -> None:
        fonts = self.root / "fonts"
        fonts.mkdir()
        (fonts / "Pixel.ttf").write_bytes(b"fake-font")
        (fonts / "fonts.json").write_text(json.dumps([
            {"file": "Pixel.ttf", "family": "Pixel Mono", "fullName": "Pixel Mono Regular"}]), encoding="utf-8")
        source = json.loads(SAMPLE.read_text(encoding="utf-8"))
        source["meta"]["fontFamily"] = "Pixel Mono|Regular"
        sample = self.root / "font.json"
        sample.write_text(json.dumps(source, ensure_ascii=False), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            code = preview.main([str(sample), "-o", str(self.root / "site"), "--fonts", str(fonts)])
        self.assertEqual(code, 0)
        site = read_site(self.root / "site")
        self.assertEqual(site["fontNames"], ["Pixel Mono Regular"])
        css = (self.root / "site" / "assets" / "fonts.css").read_text(encoding="utf-8")
        self.assertIn("@font-face", css)
        self.assertIn("url('../fonts/Pixel.ttf')", css)
        self.assertTrue((self.root / "site" / "fonts" / "Pixel.ttf").is_file())

    def test_foreign_output_directory_is_refused(self) -> None:
        foreign = self.root / "foreign"
        foreign.mkdir()
        keep = foreign / "important.txt"
        keep.write_text("do not delete", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            code = preview.main([str(SAMPLE), "-o", str(foreign)])
        self.assertEqual(code, 2)
        self.assertEqual(keep.read_text(encoding="utf-8"), "do not delete")

    def test_only_typical_skips_rest_rows_and_chunks(self) -> None:
        with contextlib.redirect_stdout(io.StringIO()):
            code = preview.main([str(SAMPLE), "--only-typical", "-o", str(self.root / "site")])
        self.assertEqual(code, 0)
        site = read_site(self.root / "site")
        self.assertEqual(site["rest"], [])
        self.assertFalse((self.root / "site" / "data" / "doc-0-rest.js").exists())


if __name__ == "__main__":
    unittest.main()
