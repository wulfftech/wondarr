#!/usr/bin/env python3
"""Fail when a source file with a "Ported from" header is missing from NOTICE.md (CODING_STANDARDS.md).

    python scripts/check-notice.py          # check (CI)
    python scripts/check-notice.py --fix    # append rows for missing files, built from their headers
"""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
HEADER = re.compile(r"^\s*(//|#)\s*Ported from ([^(]+?) \(", re.MULTILINE)
ROW = re.compile(r"^\s*(?://|#)\s*Ported from (\w+) \((https://[^)]+)\), ([^,]+), (GPL-3\.0|MIT)", re.MULTILINE)
SOURCES = ("src", "frontend/src")
SUFFIXES = {".cs", ".ts", ".tsx", ".py", ".sh"}


def ported_files() -> list[str]:
    found = []
    for root in SOURCES:
        base = REPO / root
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*")):
            if path.is_file() and path.suffix in SUFFIXES and "node_modules" not in path.parts:
                if HEADER.search(path.read_text(encoding="utf-8", errors="replace")[:2000]):
                    found.append(path.relative_to(REPO).as_posix())
    return found


def main() -> int:
    notice_path = REPO / "NOTICE.md"
    notice = notice_path.read_text(encoding="utf-8")
    missing = [rel for rel in ported_files() if f"`{rel}`" not in notice]
    if missing and "--fix" in sys.argv:
        rows = []
        for rel in missing:
            match = ROW.search((REPO / rel).read_text(encoding="utf-8", errors="replace")[:2000])
            if match:
                rows.append(f"| {match.group(1)} ({match.group(2)}) | {match.group(4)} | `{match.group(3)}` | `{rel}` |")
            else:
                print(f"cannot parse the header of {rel}; add its row by hand")
        lines = notice.splitlines()
        last = max(i for i, line in enumerate(lines) if line.startswith("| ") and "`src/" in line)
        lines[last + 1:last + 1] = rows
        notice_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        print(f"added {len(rows)} NOTICE.md rows")
        return 0 if len(rows) == len(missing) else 1
    for rel in missing:
        print(f"NOTICE.md does not list ported file {rel}")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
