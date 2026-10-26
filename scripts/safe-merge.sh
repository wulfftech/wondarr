#!/usr/bin/env bash
# Merges a worker branch into the current branch only onto a green tree, and keeps the merge only when
# the merged tree is green too (never merge red; parallel tasks conflict semantically, so the
# merged tree is built and tested before the merge commit exists).
#
#   scripts/safe-merge.sh <branch> ["Merge P3-01: reference-library scan"]
#
# The merge is made with --no-commit and aborted (git merge --abort) when the checks fail, so a red
# merge never lands. The pre-merge check is skipped when the current HEAD already passed this script
# (recorded in .worker/last-green). Frontend checks run when the branch touches frontend/, on a
# node_modules reinstalled whenever it was not installed from the tree's package-lock.json.
#
# Flaky tests (owner policy, code review 2026-10-10 R-044): a failed test run re-runs only the tests that
# failed, once, on their own. The re-run is accepted only when the failed test names captured from the log
# account for EVERY failure in the summary's "Failed:" totals; a failure the parser could not name stays
# red. Each accepted flaky test is printed in a marked FLAKY TESTS block, so the orchestrator can log it
# to the backlog. The parsing functions are tested by scripts/tests/test-safe-merge-parsing.sh, which
# sources this file (nothing runs on source).
#
# The checks run on the test host when one is set (scripts/remote-run.sh, .worker/test-host.env), in a copy
# of the tree as it stands, the uncommitted merge included; the merge itself, its commit and the marker stay
# here. Without a host they run here, as before.
#
#   scripts/safe-merge.sh --check [--frontend]     # only the checks, on the tree in the current directory
set -euo pipefail

# --- log parsing --------------------------------------------------------------------------------
# These read a `dotnet test` log (xUnit.net through vstest). A failed case shows up as
#   [xUnit.net 00:00:12.34]     Ns.Outer+Inner.Method(input: "x") [FAIL]      (theory case, nested class)
#   [xUnit.net 00:00:12.34]     Ns.Class.Method [FAIL]
#   Failed Ns.Class.Method(input: "x") [12 ms]                                (the vstest line, same name)
# and each project ends with a summary line
#   Failed!  - Failed:     2, Passed:  1788, Skipped:     3, Total:  1793, Duration: 1 m 11 s - X.dll (net10.0)

# One line per failed test case (its full display name, arguments included), sorted and unique.
failed_case_names() {
    local log="$1"
    {
        sed -nE 's/^\[xUnit\.net [0-9:.]+\] +(.+) \[FAIL\]\r?$/\1/p' "${log}"
        sed -nE 's/^ +Failed (.+) \[[^]]*\]\r?$/\1/p' "${log}"
    } | sort -u
}

# The test method behind each case name: everything up to the first "(" (a theory case's arguments),
# unique. A nested class keeps its "+" (Ns.Outer+Inner.Method).
failed_method_names() {
    sed -E 's/\(.*$//; s/[[:space:]]+$//' | sort -u
}

# The sum of the "Failed:" counts of every project summary line in the log.
summary_failed_total() {
    sed -nE 's/^(Failed|Passed)! +- Failed: +([0-9]+),.*/\2/p' "$1" | awk '{ n += $1 } END { print n + 0 }'
}

# The sum of the "Total:" counts of every project summary line in the log.
summary_total() {
    sed -nE 's/^(Failed|Passed)! +.*Total: +([0-9]+),.*/\2/p' "$1" | awk '{ n += $1 } END { print n + 0 }'
}

# How many projects ended with a "Passed!" summary line.
projects_passed() {
    grep -cE '^Passed! ' "$1" || true
}

# True when a test host died while tests were still running: vstest then prints "Test Run Aborted." and names
# the tests that were running. A project cut short like that still prints a "Passed!" line for the few tests
# it ran (6 of ~1,500 in the M-50 merge, 2026-10-16), so its summary cannot be trusted. A host that only drops
# the socket after an all-green run prints neither marker.
run_aborted_mid_run() {
    grep -qE '^Test Run Aborted\.|The tests? running when the crash occurred' "$1"
}

# How many projects ended with any summary line (Passed! or Failed!). A project whose test host crashed prints none.
projects_reported() {
    grep -cE '^(Passed|Failed)! ' "$1" || true
}

# True when every captured failed case is accounted for by the summary totals: the log names exactly as
# many distinct failed cases as the summaries count as failed (and at least one).
failures_accounted_for() {
    local log="$1" named total
    named="$(failed_case_names "${log}" | grep -c . || true)"
    total="$(summary_failed_total "${log}")"
    [[ "${total}" -gt 0 && "${named}" -eq "${total}" ]]
}

# The vstest filter that selects the given test methods (one per line on stdin).
rerun_filter() {
    local filter="" method
    while IFS= read -r method; do
        [[ -n "${method}" ]] || continue
        filter+="FullyQualifiedName=${method}|"
    done
    printf '%s' "${filter%|}"
}

# How many test projects the solution holds (the ones under tests/), instead of a number written down here.
count_test_projects() {
    dotnet sln list 2>/dev/null | grep -cE '^tests[/\\]' || true
}

# --- the frontend install -----------------------------------------------------------------------
# The lockfile node_modules was installed from, as its git object id, kept inside node_modules: npm ci
# empties the folder first, so a stamp never outlives the install it describes. --no-filters hashes the
# bytes on disk, so Git Bash and WSL's git agree whatever their line-ending settings.
frontend_lock_stamp="node_modules/.safe-merge-lock"

# True when the node_modules in the current directory was installed from its package-lock.json. A tree
# whose lockfile moved (a dependency bump, or a refused merge that left the branch's packages behind)
# must be reinstalled, or its checks run against the old packages and pass without testing the new
# ones (the Dependabot bumps, 2026-10-21: vitest 5.0.4 would have been checked on 5.0.2).
frontend_install_current() {
    [[ -f "${frontend_lock_stamp}" && -f package-lock.json ]] \
        && [[ "$(cat "${frontend_lock_stamp}")" == "$(git hash-object --no-filters package-lock.json)" ]]
}

# Installs node_modules from package-lock.json unless it already was, and stamps it.
frontend_install() {
    if frontend_install_current; then
        return 0
    fi
    echo "safe-merge: npm ci (node_modules was not installed from this package-lock.json)"
    npm ci --no-audit --no-fund || return 1
    git hash-object --no-filters package-lock.json > "${frontend_lock_stamp}"
}

# --- the checks ----------------------------------------------------------------------------------

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
    # a grab conflict, a shares render — each green alone), hence the single re-run described above.
    local log rerun_log names methods filter expected
    log="$(mktemp)"
    if ! VSTEST_CONNECTION_TIMEOUT=300 dotnet test --no-build --nologo -v q -m:1 --blame-hang-timeout 5m --blame-hang-dump-type none 2>&1 | tee "${log}"; then
        names="$(failed_case_names "${log}")"
        if [[ -z "${names}" ]]; then
            # No failed test is named. If every project reported "Passed!" and none "Failed!", the exit
            # code came from vstest's own plumbing (its data collector dropping the socket on a starved
            # machine), not a test.
            expected="$(count_test_projects)"
            if run_aborted_mid_run "${log}"; then
                echo "safe-merge: a test host crashed while tests were running; that project's summary covers only part of it, so the run is red (re-run the merge)" >&2
                rm -f "${log}"
                return 1
            fi
            if grep -q 'Failed!' "${log}" || [[ "$(summary_failed_total "${log}")" -gt 0 ]] \
                || [[ "${expected}" -lt 1 ]] || [[ "$(projects_passed "${log}")" -lt "${expected}" ]]; then
                echo "safe-merge: the test run failed and no failed test is named (${expected} test projects expected, $(projects_passed "${log}") passed)" >&2
                rm -f "${log}"
                return 1
            fi
            echo "safe-merge: every test project passed; the non-zero exit came from the test host plumbing"
        else
            expected="$(count_test_projects)"
            if [[ "${expected}" -lt 1 || "$(projects_reported "${log}")" -ne "${expected}" ]] || run_aborted_mid_run "${log}"; then
                echo "safe-merge: refusing the re-run — $(projects_reported "${log}") of ${expected} test projects reported a summary, or a test host crashed mid-run; a project whose host crashed cannot be vouched for" >&2
                rm -f "${log}"
                return 1
            fi
            if ! failures_accounted_for "${log}"; then
                echo "safe-merge: refusing the re-run — the log names $(printf '%s\n' "${names}" | grep -c .) failed test case(s) but the summaries count $(summary_failed_total "${log}") failed; a failure the parser cannot name is not flaky" >&2
                rm -f "${log}"
                return 1
            fi
            methods="$(printf '%s\n' "${names}" | failed_method_names)"
            filter="$(printf '%s\n' "${methods}" | rerun_filter)"
            echo "safe-merge: re-running the failed tests once on their own:"
            printf '%s\n' "${methods}" | sed 's/^/safe-merge:   /'
            rerun_log="$(mktemp)"
            if ! VSTEST_CONNECTION_TIMEOUT=300 dotnet test --no-build --nologo -v q -m:1 --filter "${filter}" 2>&1 | tee "${rerun_log}"; then
                rm -f "${log}" "${rerun_log}"
                return 1
            fi
            # A filter that matches nothing exits 0 too: the re-run must have actually run the cases.
            if [[ "$(summary_total "${rerun_log}")" -lt "$(printf '%s\n' "${names}" | grep -c .)" ]]; then
                echo "safe-merge: refusing — the re-run ran fewer tests than failed, so the filter did not select them" >&2
                rm -f "${log}" "${rerun_log}"
                return 1
            fi
            rm -f "${rerun_log}"
            echo "safe-merge: ===== FLAKY TESTS (failed under load, passed alone; log them to the backlog) ====="
            printf '%s\n' "${methods}" | sed 's/^/safe-merge: FLAKY-TEST: /'
            echo "safe-merge: ===== END FLAKY TESTS ====="
        fi
    fi
    rm -f "${log}"
    if [[ "${run_frontend}" == "1" ]]; then
        echo "safe-merge: frontend checks"
        (
            cd frontend
            frontend_install || exit 1
            npm run lint && npm run typecheck && npm run format && npm test -- --maxWorkers=2 && npm run build
        ) || return 1
    fi
}

# The checks for the tree in the working directory, on the test host when one is set, else here.
run_check() {
    local arguments=(--check)
    # A tree from before the test host has no remote-run.sh to send itself with; it is checked here.
    if [[ ! -f scripts/remote-run.sh ]]; then
        check
        return
    fi
    if [[ "${run_frontend}" == "1" ]]; then
        arguments+=(--frontend)
    fi
    scripts/remote-run.sh bash scripts/safe-merge.sh "${arguments[@]}"
}

main() {
    local branch message root marker head_before
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

    run_frontend=0
    if ! git diff --quiet "HEAD...${branch}" -- frontend/; then
        run_frontend=1
    fi

    head_before="$(git rev-parse HEAD)"
    if [[ -f "${marker}" && "$(cat "${marker}")" == "${head_before}" && "${run_frontend}" == "0" ]]; then
        echo "safe-merge: ${head_before:0:8} already passed; skipping the pre-merge check"
    else
        echo "safe-merge: checking the current tree (${head_before:0:8})"
        if ! run_check; then
            echo "safe-merge: refusing — the current tree is red; fix it before merging" >&2
            exit 1
        fi
    fi

    if ! git merge --no-ff --no-commit "${branch}"; then
        echo "safe-merge: the merge has conflicts; resolve them, run the checks, then commit (or git merge --abort)" >&2
        exit 1
    fi

    echo "safe-merge: checking the merged tree"
    if run_check; then
        git commit --no-edit -m "${message}"
        mkdir -p "$(dirname "${marker}")"
        git rev-parse HEAD > "${marker}"
        echo "safe-merge: merged ${branch} as $(git rev-parse --short HEAD)"
    else
        git merge --abort
        echo "safe-merge: refused — the merged tree is red; the merge was aborted" >&2
        exit 1
    fi
}

# Run only when executed, not when scripts/tests/test-safe-merge-parsing.sh sources the functions above.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    if [[ "${1:-}" == "--check" ]]; then
        run_frontend=0
        if [[ "${2:-}" == "--frontend" ]]; then
            run_frontend=1
        fi
        check
    else
        main "$@"
    fi
fi
