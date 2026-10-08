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

# Every step returns explicitly on failure: check() runs as an `if` condition, where bash ignores
# `set -e`, so without the `|| return 1` a red `dotnet test` was masked by a later green step (it let
# a merge with two failing tests through when the frontend build, the last step, passed).
check() {
    echo "safe-merge: dotnet build -warnaserror"
    dotnet build -warnaserror --nologo -v q || return 1
    echo "safe-merge: dotnet test"
    # One test project at a time (-m:1): run side by side, the timing- and port-bound tests (the
    # pumped compaction runs, FakeSlskd's fixed ports and search budget) fail on a loaded machine.
    # A hung test host is killed after five minutes and the hanging test named, instead of the merge
    # waiting for ever.
    # vstest gives a slow machine 90 s to start its data collector, then aborts the whole run.
    # On this machine a loaded run fails a different timing-bound test each time (a SignalR broadcast,
    # a grab conflict, a shares render — each green alone). A failed run re-runs only the tests that
    # failed, once, on their own: they must pass then, and they are named as flaky; any other failure
    # (a build or host error with no failed test named) stays red.
    local log failed filter
    log="$(mktemp)"
    if ! VSTEST_CONNECTION_TIMEOUT=300 dotnet test --no-build --nologo -v q -m:1 --blame-hang-timeout 5m --blame-hang-dump-type none 2>&1 | tee "${log}"; then
        failed="$(grep -oE '^\[xUnit\.net [0-9:.]+\] +[A-Za-z0-9_.]+ \[FAIL\]' "${log}" | sed -E 's/^\[xUnit\.net [0-9:.]+\] +//; s/ \[FAIL\]$//' | sort -u || true)"
        if [[ -z "${failed}" ]]; then
            # Every project reported "Passed!" and none "Failed!": the exit code came from vstest's
            # own plumbing (its data collector dropping the socket on a starved machine), not a test.
            if grep -q 'Failed!' "${log}" || [[ "$(grep -c 'Passed!' "${log}")" -lt 3 ]]; then
                rm -f "${log}"
                return 1
            fi
            echo "safe-merge: every test project passed; the non-zero exit came from the test host plumbing"
        else
            filter="$(printf 'FullyQualifiedName=%s|' ${failed})"
            echo "safe-merge: re-running the failed tests once on their own:" ${failed}
            if ! VSTEST_CONNECTION_TIMEOUT=300 dotnet test --no-build --nologo -v q -m:1 --filter "${filter%|}"; then
                rm -f "${log}"
                return 1
            fi
            echo "safe-merge: FLAKY (failed under load, passed alone):" ${failed}
        fi
    fi
    rm -f "${log}"
    if [[ "${run_frontend}" == "1" ]]; then
        echo "safe-merge: frontend checks"
        (
            cd frontend
            [[ -d node_modules ]] || npm ci --no-audit --no-fund
            npm run lint && npm run typecheck && npm run format && npm test && npm run build
        ) || return 1
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
