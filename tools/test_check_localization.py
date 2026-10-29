"""Consumer regressions for localization validation; no game or third-party packages."""
from __future__ import annotations

import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import check_localization as checker


class LocalizationValidationTests(unittest.TestCase):
    def setUp(self) -> None:
        test_output = Path(__file__).resolve().parent.parent / "out" / "localization-tests"
        test_output.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=test_output)
        self.root = Path(self.temp.name)
        if not self.root.resolve().is_relative_to(test_output.resolve()):
            raise RuntimeError("Temporary test path escaped its output directory")
        self.addCleanup(self.temp.cleanup)
        self.loc = self.root / "Localization"
        self.loc.mkdir()
        self.source = self.root / "Entry" / "ModEntry.cs"
        self.source.parent.mkdir()
        self.source.write_text('Loc.T("probe", "English {0}");', encoding="utf-8")
        self.csv = self.loc / "zh.csv"

    def validate(self, strict: bool = False) -> int:
        args = ["check_localization.py"] + (["--strict-placeholders"] if strict else [])
        with patch.object(checker, "ROOT", self.root), \
                patch.object(checker, "LOC_DIR", self.loc), \
                patch.object(checker, "INDIRECT_KEYS", {}), \
                patch("sys.argv", args), contextlib.redirect_stdout(io.StringIO()):
            return checker.main()

    def test_duplicate_key_fails(self) -> None:
        self.csv.write_text("probe,One {0}\nprobe,Two {0}\n", encoding="utf-8")
        self.assertEqual(1, self.validate(strict=True))

    def test_missing_argument_fails_in_both_modes(self) -> None:
        self.csv.write_text("probe,Missing number\n", encoding="utf-8")
        for strict in (False, True):
            with self.subTest(strict=strict):
                self.assertEqual(1, self.validate(strict))

    def test_missing_formatted_argument_fails(self) -> None:
        self.source.write_text('Loc.T("probe", "Plant {0:D2}");', encoding="utf-8")
        self.csv.write_text("probe,Missing plant number\n", encoding="utf-8")
        self.assertEqual(1, self.validate(strict=True))

    def test_extra_argument_fails(self) -> None:
        self.csv.write_text("probe,Extra {0} {1}\n", encoding="utf-8")
        self.assertEqual(1, self.validate())

    def test_malformed_extra_row_fails(self) -> None:
        self.csv.write_text("probe,Valid {0}\nmissing-comma\n", encoding="utf-8")
        self.assertEqual(1, self.validate(strict=True))

    def test_empty_localization_directory_fails(self) -> None:
        self.assertEqual(1, self.validate(strict=True))

    def test_empty_csv_key_fails(self) -> None:
        self.csv.write_text("probe,Valid {0}\n,Invalid\n", encoding="utf-8")
        self.assertEqual(1, self.validate())

    def test_formatted_and_aligned_arguments_preserve_parity(self) -> None:
        self.source.write_text('Loc.T("probe", "First {0:D2}, second {1}");', encoding="utf-8")
        self.csv.write_text('probe,"Second {1}, first {0,4:D2}"\n', encoding="utf-8")
        self.assertEqual(0, self.validate())

    def test_escaped_braces_do_not_introduce_arguments(self) -> None:
        self.source.write_text('Loc.T("probe", "Literal {{3}}, number {0:D2}");', encoding="utf-8")
        self.csv.write_text('probe,"Literal {{8}}, number {0,-4:D2}"\n', encoding="utf-8")
        self.assertEqual(0, self.validate(strict=True))

    def test_invalid_translated_format_fails(self) -> None:
        self.csv.write_text("probe,Broken {0\n", encoding="utf-8")
        self.assertEqual(1, self.validate())

    def test_generated_and_test_sources_do_not_define_runtime_keys(self) -> None:
        self.csv.write_text("probe,Valid {0}\n", encoding="utf-8")
        for folder in ("out", "tools", "bin", "obj"):
            path = self.root / folder / "Generated.cs"
            path.parent.mkdir()
            path.write_text('Loc.T("generated", "Generated");', encoding="utf-8")
        self.assertEqual(0, self.validate())


if __name__ == "__main__":
    unittest.main()
