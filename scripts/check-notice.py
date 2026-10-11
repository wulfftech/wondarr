#!/usr/bin/env python3
"""Fail when ported code and NOTICE.md disagree (CODING_STANDARDS.md).

Two checks:

* a source file whose first comment block says "ported from", "adapted from" or "derived from"
  (any case, anywhere in the block) must be listed in NOTICE.md;
* every row of NOTICE.md's ported-code table must name a file that still exists.

    python scripts/check-notice.py          # check (CI)
    python scripts/check-notice.py --fix    # append rows for missing files, built from their headers
"""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
HEADER = re.compile(r"\b(?:ported|adapted|derived)\s+from\b", re.IGNORECASE)
ROW = re.compile(r"^\s*(?://|#)\s*Ported from (\w+) \((https://[^)]+)\), ([^,\n]+), (GPL-3\.0|MIT)", re.MULTILINE)
SOURCES = ("src", "frontend/src", "tools", "docker", "scripts")
SUFFIXES = {".cs", ".ts", ".tsx", ".py", ".sh"}
NAMES = {"Dockerfile"}
SKIPPED_DIRS = {"node_modules", "bin", "obj"}
READ_BYTES = 8192
# The roots a NOTICE row's "where in this repo" column may point into.
ROOTS = tuple(root.split("/")[0] + "/" for root in SOURCES)


def read_head(path: Path) -> str:
    with path.open("rb") as handle:
        return handle.read(READ_BYTES).decode("utf-8", errors="replace")


def first_comment_block(text: str) -> str:
    """The leading run of comment lines (`//`, `#`, or a `/* */` block), after any shebang or BOM."""
    block = []
    in_block_comment = False
    for index, line in enumerate(text.lstrip("﻿").splitlines()):
        stripped = line.strip()
        if index == 0 and stripped.startswith("#!"):
            continue
        if in_block_comment:
            block.append(stripped)
            if "*/" in stripped:
                in_block_comment = False
            continue
        if stripped.startswith("/*"):
            block.append(stripped)
            in_block_comment = "*/" not in stripped
            continue
        if stripped.startswith(("//", "#")):
            block.append(stripped)
            continue
        if not stripped and not block:
            continue
        break
    return "\n".join(block)


def is_scanned(path: Path) -> bool:
    return path.is_file() and (path.suffix in SUFFIXES or path.name in NAMES) and not SKIPPED_DIRS & set(path.parts)


def ported_files() -> list[str]:
    found = []
    for root in SOURCES:
        base = REPO / root
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*")):
            if is_scanned(path) and HEADER.search(first_comment_block(read_head(path))):
                found.append(path.relative_to(REPO).as_posix())
    return found


def listed_files(notice: str) -> list[tuple[int, str]]:
    """(line number, path) for each file the last column of the ported-code table names."""
    listed = []
    in_table = False
    for number, line in enumerate(notice.splitlines(), start=1):
        if line.startswith("## "):
            in_table = line.startswith("## Ported")
            continue
        if not in_table or not line.startswith("| "):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        for token in re.findall(r"`([^`]+)`", cells[-1]):
            if token.startswith(ROOTS):
                listed.append((number, token))
    return listed


def main() -> int:
    notice_path = REPO / "NOTICE.md"
    notice = notice_path.read_text(encoding="utf-8")
    missing = [rel for rel in ported_files() if f"`{rel}`" not in notice]
    if missing and "--fix" in sys.argv:
        rows = []
        for rel in missing:
            match = ROW.search(read_head(REPO / rel))
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

    failed = False
    for rel in missing:
        print(f"NOTICE.md does not list ported file {rel}")
        failed = True
    for number, rel in listed_files(notice):
        if not (REPO / rel).is_file():
            print(f"NOTICE.md line {number} lists {rel}, which does not exist")
            failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
