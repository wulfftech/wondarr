#!/usr/bin/env bash
# Merges a worker branch into the current branch only onto a green tree, and keeps the merge only when
# the merged tree is green too (docs/build/AGENT_WORKFLOW.md §4: never merge red; parallel tasks
# conflict semantically, so the merged tree is built and tested before the merge commit exists).
#
#   scripts/safe-merge.sh <branch> ["Merge P3-01: reference-library scan"]
#
# The merge is made with --no-commit and aborted (git merge --abort) when the checks fail, so a red
# merge never lands. The pre-merge check is skipped when the current HEAD already passed this script
# (recorded in .worker/last-green). Frontend checks run when the branch touches frontend/.
set -euo pipefail

branch="${1:?usage: scripts/safe-merge.sh <branch> [message]}"
message="${2:-Merge ${branch}}"
root="$(git rev-parse --show-toplevel)"
marker="${root}/.worker/last-green"
cd "${root}"

if ! git rev-parse --verify --quiet "${branch}^{commit}" >/dev/null; then
    echo "safe-merge: refusing — '${branch}' is not a branch or commit" >&2
    exit 1
fi

if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "safe-merge: refusing — the working tree has uncommitted changes" >&2
    exit 1
fi

touches_frontend() {
    ! git diff --quiet "HEAD...${branch}" -- frontend/
}

check() {
    echo "safe-merge: dotnet build -warnaserror"
    dotnet build -warnaserror --nologo -v q
    echo "safe-merge: dotnet test"
    dotnet test --no-build --nologo -v q
    if [[ "${run_frontend}" == "1" ]]; then
        echo "safe-merge: frontend checks"
        (
            cd frontend
            [[ -d node_modules ]] || npm ci --no-audit --no-fund
            npm run lint && npm run typecheck && npm test && npm run build
        )
    fi
}

run_frontend=0
if touches_frontend; then
    run_frontend=1
fi

head_before="$(git rev-parse HEAD)"
if [[ -f "${marker}" && "$(cat "${marker}")" == "${head_before}" && "${run_frontend}" == "0" ]]; then
    echo "safe-merge: ${head_before:0:8} already passed; skipping the pre-merge check"
else
    echo "safe-merge: checking the current tree (${head_before:0:8})"
    if ! check; then
        echo "safe-merge: refusing — the current tree is red; fix it before merging" >&2
        exit 1
    fi
fi

if ! git merge --no-ff --no-commit "${branch}"; then
    echo "safe-merge: the merge has conflicts; resolve them, run the checks, then commit (or git merge --abort)" >&2
    exit 1
fi

echo "safe-merge: checking the merged tree"
if check; then
    git commit --no-edit -m "${message}"
    mkdir -p "$(dirname "${marker}")"
    git rev-parse HEAD > "${marker}"
    echo "safe-merge: merged ${branch} as $(git rev-parse --short HEAD)"
else
    git merge --abort
    echo "safe-merge: refused — the merged tree is red; the merge was aborted" >&2
    exit 1
fi
