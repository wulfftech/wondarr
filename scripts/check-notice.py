#!/usr/bin/env python3
"""Fail when a source file with a "Ported from" header is missing from NOTICE.md (CODING_STANDARDS.md)."""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
HEADER = re.compile(r"^\s*(//|#)\s*Ported from ([^(]+?) \(", re.MULTILINE)


def main() -> int:
    notice = (REPO / "NOTICE.md").read_text(encoding="utf-8")
    missing = []
    for path in sorted((REPO / "src").rglob("*")) + sorted((REPO / "frontend" / "src").rglob("*")):
        if not path.is_file() or path.suffix not in {".cs", ".ts", ".tsx", ".py", ".sh"}:
            continue
        if HEADER.search(path.read_text(encoding="utf-8", errors="replace")[:2000]):
            rel = path.relative_to(REPO).as_posix()
            if f"`{rel}`" not in notice:
                missing.append(rel)
    for rel in missing:
        print(f"NOTICE.md does not list ported file {rel}")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
