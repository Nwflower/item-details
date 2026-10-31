#!/usr/bin/env python3
"""校验 Item Details 的本地化词条与源码键是否一致。

检查项：
  1. 源码里用到的每个键，在每份 Localization/*.csv 里都必须有译文；
  2. CSV 里不能有源码中已不存在的键（改键名/删功能后留下的孤儿词条）；
  3. 译文的占位符集合必须与英文兜底一致（`{0}` `{1}` … 不能多、不能少、不能改编号）；
  4. 同一份 CSV 内键不能重复，值不能为空；
  5. 译文不能用游戏字体（NotoSansSC）不覆盖的装饰符号与 emoji——TMP 找不到字形会
     替换成 □，并且每重建一次文本就往 Player.log 写一条警告（实测 U+2714/U+2718）。

用法（在 Mod 仓库根目录）：
    python tools/check_localization.py
    python tools/check_localization.py --strict-placeholders
退出码 0 = 通过，1 = 有问题。
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LOC_DIR = ROOT / "Localization"

# 源码里的取词入口：Loc.T("key", "English …") 与设置面板的 Text("key", "English …")
CALL_PATTERNS = (
    re.compile(r'Loc\.T\(\s*"([a-z][a-z0-9_.]*)"\s*,\s*((?:\$?"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+)'),
    re.compile(r'(?<![A-Za-z0-9_])Text\(\s*"([a-z][a-z0-9_.]*)"\s*,\s*((?:\$?"(?:[^"\\]|\\.)*"\s*(?:\+\s*)?)+)'),
)
LIT = re.compile(r'\$?"((?:[^"\\]|\\.)*)"')
# Composite items used by the runtime, including alignment and formats such as {0:D2}.
FORMAT_ITEM = re.compile(r"\{([0-9]+) *(?:, *-?[0-9]+ *)?(?::[^{}]*)?\}")
EXCLUDED_SOURCE_DIRS = {"out", "tools", "tests", "bin", "obj", ".git"}

# 间接引用的键：常量传给 RefreshSections 的循环变量，字面量不出现在 Loc.T(...) 里。
# 少了它们，设置面板的分组标题在非英语下仍是英文，而校验器还会说「齐全」。
INDIRECT_KEYS = {
    "config.section.display": "Display",
    "config.section.reveal": "Reveal",
    "config.section.interaction": "Interaction & Visuals",
}


def source_keys() -> dict[str, str]:
    """返回 {key: 英文兜底模板}。同一键多处使用时取第一次出现的英文。"""
    found: dict[str, str] = {}
    for path in sorted(ROOT.rglob("*.cs")):
        if EXCLUDED_SOURCE_DIRS.intersection(path.relative_to(ROOT).parts):
            continue
        text = path.read_text(encoding="utf-8")
        for pat in CALL_PATTERNS:
            for m in pat.finditer(text):
                key = m.group(1)
                en = "".join(LIT.findall(m.group(2)))
                found.setdefault(key, en)
        for key, en in pure_keys(text).items():
            found.setdefault(key, en)
    for key, en in INDIRECT_KEYS.items():
        found.setdefault(key, en)
    return found


def pure_keys(text: str) -> dict[str, str]:
    """纯逻辑层（src/Domain/*.cs）里以数据形式出现的键。

    这些键不经过 Loc.T 调用点：
      - ValueFormula.Pure.cs：`new("appraisal.xxx", "English {0}", amount, ...)`
      - RibwichReasons.Pure.cs：Key() 与 English() 两个同序 switch
      - Authenticity.cs：`new("ISSUE_SERIAL", "auth.serial", "Serial fault")`
      - ModuleStatEffects.cs：`PerformanceKey = "module.x", PerformanceEn = "English"` 等表项
    不扫它们，校验器会给出「全部齐全」的假结论。
    """
    out: dict[str, str] = {}
    for m in re.finditer(r'new\(\s*"((?:appraisal|ribwich)\.[a-z0-9_]+)"\s*,\s*"((?:[^"\\]|\\.)*)"', text):
        out.setdefault(m.group(1), m.group(2))

    # switch 表达式里的 (key, English) 元组：漏扫会让厂商名等标签一直回落英文。
    for m in re.finditer(r'=>\s*\(\s*"((?:appraisal|ribwich)\.[a-z0-9_]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)', text):
        out.setdefault(m.group(1), m.group(2))

    # 真伪检查项的键是第二个参数，不是第一个；漏扫会让赝品括号里的问题名全部回落英文。
    for m in re.finditer(r'new\(\s*"[A-Z0-9_]+"\s*,\s*"(auth\.[a-z0-9_]+)"\s*,\s*"((?:[^"\\]|\\.)*)"', text):
        out.setdefault(m.group(1), m.group(2))

    # 机器能力表把键和英文兜底放在 Profile 字段里，再由变量传给 Loc.T。
    for m in re.finditer(r'(?:Performance|Quality)Key\s*=\s*"(module\.[a-z0-9_]+)"\s*,\s*(?:Performance|Quality)En\s*=\s*"((?:[^"\\]|\\.)*)"', text):
        out.setdefault(m.group(1), m.group(2))

    marker_key = "internal static string Key"
    marker_en = "internal static string English"
    marker_args = "internal static object[] Args"
    if marker_key in text and marker_en in text and marker_args in text:
        keys_part = text[text.index(marker_key):text.index(marker_en)]
        en_part = text[text.index(marker_en):text.index(marker_args)]
        keys = re.findall(r'=>\s*"((?:appraisal|ribwich)\.[a-z0-9_]+)"', keys_part)
        ens = re.findall(r'=>\s*"((?:[^"\\]|\\.)*)"', en_part)
        if len(keys) == len(ens):
            for k, e in zip(keys, ens):
                out.setdefault(k, e)
        else:
            print(f"警告：{marker_key} 与 {marker_en} 的条数不一致（{len(keys)} vs {len(ens)}），已跳过配对")
    return out


def parse_csv(path: Path) -> tuple[dict[str, str], int]:
    """与运行时 Loc.Table 相同的解析规则：只按第一个逗号切分。"""
    rows: dict[str, str] = {}
    dupes: list[str] = []
    problems = 0
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip("\ufeff")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if "," not in line:
            print(f"{path.name}:{lineno}: 缺少逗号，已跳过")
            problems += 1
            continue
        key, _, value = line.partition(",")
        key = key.strip()
        if not key:
            print(f"{path.name}:{lineno}: key 为空，已跳过")
            problems += 1
            continue
        value = value.strip()
        if value.startswith('"') and value.endswith('"') and len(value) >= 2:
            value = value[1:-1].replace('""', '"')
        value = value.replace("\\n", "\n")
        if key in rows:
            dupes.append(key)
        rows[key] = value
    if dupes:
        print(f"{path.name}: 重复键 {sorted(set(dupes))}")
    return rows, problems + len(dupes)


# 游戏 UI 字体 NotoSansSC 不覆盖装饰符号（Dingbats）与 emoji 区：
# TMP 找不到字形时替换成 □，并在每次文本重建时写一条警告，容易刷屏。
UNSUPPORTED_GLYPH_RANGES = ((0x2700, 0x27BF), (0x1F300, 0x1FAFF))


def unsupported_glyphs(text: str) -> set[str]:
    """返回游戏字体大概率没有字形的字符。"""
    return {ch for ch in text if any(lo <= ord(ch) <= hi for lo, hi in UNSUPPORTED_GLYPH_RANGES)}


def placeholder_indexes(text: str) -> set[int]:
    """Read composite argument indexes, ignoring escaped literal braces."""
    indexes: set[int] = set()
    pos = 0
    while pos < len(text):
        if text.startswith("{{", pos) or text.startswith("}}", pos):
            pos += 2
        elif text[pos] == "{":
            item = FORMAT_ITEM.match(text, pos)
            if item is None:
                raise ValueError(f"无效占位符，位置 {pos}: {text[pos:]}")
            indexes.add(int(item.group(1)))
            pos = item.end()
        elif text[pos] == "}":
            raise ValueError(f"未转义的右花括号，位置 {pos}")
        else:
            pos += 1
    return indexes


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict-placeholders", action="store_true",
                    help="兼容已有命令；两种模式都要求占位符编号集合一致")
    ap.parse_args()

    if not LOC_DIR.is_dir():
        print(f"找不到 {LOC_DIR}")
        return 1

    keys = source_keys()
    if not keys:
        print("源码里没有解析到任何本地化键，检查 CALL_PATTERNS 是否跟代码脱节了")
        return 1

    csvs = {p.stem: p for p in sorted(LOC_DIR.glob("*.csv"))}
    problems = 0

    print(f"源码键：{len(keys)}   语言文件：{len(csvs)}（{', '.join(csvs) or '无'}）")
    if not csvs:
        print("没有找到任何翻译 CSV")
        return 1

    for code, path in csvs.items():
        rows, parse_problems = parse_csv(path)
        file_problems = parse_problems
        missing = sorted(set(keys) - set(rows))
        orphans = sorted(set(rows) - set(keys))

        for key in missing:
            print(f"[{code}] 缺少词条：{key}")
        for key in orphans:
            print(f"[{code}] 孤儿词条（源码里已没有这个键）：{key}")

        for key in sorted(set(keys) & set(rows)):
            if not rows[key].strip():
                print(f"[{code}] 空译文：{key}")
                file_problems += 1
                continue
            try:
                want = placeholder_indexes(keys[key])
                got = placeholder_indexes(rows[key])
            except ValueError as error:
                print(f"[{code}] 格式错误：{key} {error}")
                file_problems += 1
                continue
            if want != got:
                print(f"[{code}] 占位符不一致：{key} 期望 {sorted(want)} 实际 {sorted(got)}")
                file_problems += 1

            bad = unsupported_glyphs(rows[key])
            if bad:
                shown = " ".join(f"U+{ord(ch):04X}({ch})" for ch in sorted(bad))
                print(f"[{code}] 字体不支持的符号：{key} → {shown}，会显示成 □ 并刷日志")
                file_problems += 1

        file_problems += len(missing) + len(orphans)
        if file_problems == 0:
            print(f"[{code}] OK（{len(rows)} 条）")
        problems += file_problems

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
