#!/usr/bin/env bash
# Tests scripts/check-notice.py against a throwaway tree (R-093).
#   bash scripts/tests/test-check-notice.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script="${here}/../check-notice.py"
python="$(command -v python3 || command -v python)"
failures=0

tree="$(mktemp -d)"
trap 'rm -rf "${tree}"' EXIT
mkdir -p "${tree}/scripts" "${tree}/src/App" "${tree}/tools/Fake" "${tree}/docker" "${tree}/scripts/sub"
cp "${script}" "${tree}/scripts/check-notice.py"

notice() { # rows...
    {
        printf '# NOTICE\n\n## Ported / adapted code\n\n| Source | Licence | Upstream | Where |\n|---|---|---|---|\n'
        for row in "$@"; do printf '%s\n' "${row}"; done
        printf '\n## Bundled third-party programs\n\n| Software | Version |\n|---|---|\n| ffmpeg | 9 |\n'
    } > "${tree}/NOTICE.md"
}

run() { (cd "${tree}" && "${python}" -I scripts/check-notice.py 2>&1); }

expect() { # name want_exit output_fragment
    local output status
    output="$(run)"
    status=$?
    if [[ "${status}" -eq "$2" && "${output}" == *"$3"* ]]; then
        echo "ok   - $1"
    else
        echo "FAIL - $1 (exit ${status}, wanted $2; output: ${output})" >&2
        failures=$((failures + 1))
    fi
}

row() { printf '| Lidarr (https://github.com/Lidarr/Lidarr) | GPL-3.0 | `x.cs` | `%s` |' "$1"; }

printf '// plain file\nclass A {}\n' > "${tree}/src/App/Plain.cs"
notice
expect "a tree without ported code passes" 0 ""

# The old check needed "// Ported from X (" at offset 0; prose further into the block was missed.
printf '// The search strategy is ported from spotDL\n// (https://example.test), MIT.\n\nclass B {}\n' > "${tree}/src/App/Prose.cs"
expect "lower-case prose in the first block is detected" 1 "src/App/Prose.cs"
notice "$(row src/App/Prose.cs)"
expect "once listed it passes" 0 ""

printf '// Header line one\n// Header line two\n// Adapted from Prowlarr, GPL-3.0\nclass C {}\n' > "${tree}/src/App/Later.cs"
expect "a match on a later line of the block is detected" 1 "src/App/Later.cs"
notice "$(row src/App/Prose.cs)" "$(row src/App/Later.cs)"
expect "listed again" 0 ""

# Only the first comment block counts: a mention in the body is ordinary code.
printf '// Header\n\nclass D {\n    // derived from the config\n}\n' > "${tree}/src/App/Body.cs"
expect "a mention after the first block is not a header" 0 ""

# The other roots are scanned, and the read is 8 KB deep rather than 2 000 characters.
printf '#!/usr/bin/env bash\n# derived from an upstream script\necho hi\n' > "${tree}/scripts/sub/tool.sh"
expect "scripts/ is scanned" 1 "scripts/sub/tool.sh"
printf '# Ported from X\n' > "${tree}/docker/Dockerfile"
expect "docker/ is scanned (Dockerfile has no suffix)" 1 "docker/Dockerfile"
notice "$(row src/App/Prose.cs)" "$(row src/App/Later.cs)" "$(row scripts/sub/tool.sh)" "$(row docker/Dockerfile)"
expect "all listed" 0 ""
printf 'namespace Fake;\n// adapted from somewhere\n' > "${tree}/tools/Fake/Fake.cs"
expect "a comment after code is not a header" 0 ""
{ for _ in $(seq 1 150); do printf '// filler filler filler filler filler filler filler\n'; done; printf '// adapted from far down\nclass E {}\n'; } > "${tree}/tools/Fake/Deep.cs"
expect "a header past 2 000 characters (within 8 KB) is detected" 1 "tools/Fake/Deep.cs"
rm -f "${tree}/tools/Fake/Deep.cs"

# A NOTICE row for a file that is gone.
notice "$(row src/App/Prose.cs)" "$(row src/App/Later.cs)" "$(row scripts/sub/tool.sh)" "$(row docker/Dockerfile)" "$(row src/App/Gone.cs)"
expect "a row naming a missing file fails" 1 "src/App/Gone.cs, which does not exist"

if [[ "${failures}" -gt 0 ]]; then
    echo "${failures} check(s) failed" >&2
    exit 1
fi
echo "all checks passed"
