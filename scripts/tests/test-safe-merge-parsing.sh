#!/usr/bin/env bash
# Tests the log parsing in scripts/safe-merge.sh against captured `dotnet test` output (R-044).
#   bash scripts/tests/test-safe-merge-parsing.sh
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
samples="${here}/samples"
# shellcheck source=../safe-merge.sh
source "${here}/../safe-merge.sh"
set +e # the assertions below inspect failures themselves

failures=0
pass() { echo "ok   - $1"; }
fail() { echo "FAIL - $1" >&2; failures=$((failures + 1)); }
expect_eq() { # name expected actual
    if [[ "$2" == "$3" ]]; then pass "$1"; else fail "$1: expected [$2], got [$3]"; fi
}
expect_true() { # name command...
    local name="$1"; shift
    if "$@"; then pass "${name}"; else fail "${name}: expected success"; fi
}
expect_false() { # name command...
    local name="$1"; shift
    if "$@"; then fail "${name}: expected failure"; else pass "${name}"; fi
}

# --- a theory case: the name is captured with its arguments, the method without -----------------------
log="${samples}/theory-failure.txt"
expect_eq "theory: one failed case, arguments kept" \
    'Wondarr.Core.Tests.Organizer.NamingTests.Renders_tokens(template: "{Artist} - {Title}", expected: "A - B")' \
    "$(failed_case_names "${log}")"
expect_eq "theory: the method is cut at the first parenthesis" \
    "Wondarr.Core.Tests.Organizer.NamingTests.Renders_tokens" \
    "$(failed_case_names "${log}" | failed_method_names)"
expect_eq "theory: the summary counts one failure" "1" "$(summary_failed_total "${log}")"
expect_true "theory: every failure is accounted for" failures_accounted_for "${log}"

# --- a nested class keeps its "+" --------------------------------------------------------------------
log="${samples}/nested-class.txt"
expect_eq "nested class: the method name keeps Outer+Inner" \
    "Wondarr.Core.Tests.Jobs.CommandQueueTests+Cancelling.A_queued_command_can_be_cancelled" \
    "$(failed_case_names "${log}" | failed_method_names)"
expect_true "nested class: every failure is accounted for" failures_accounted_for "${log}"
expect_eq "nested class: the re-run filter selects it" \
    "FullyQualifiedName=Wondarr.Core.Tests.Jobs.CommandQueueTests+Cancelling.A_queued_command_can_be_cancelled" \
    "$(failed_case_names "${log}" | failed_method_names | rerun_filter)"

# --- a flaky Fact next to a really broken theory: both are named, so both are re-run ------------------
log="${samples}/mixed-flaky-and-real.txt"
expect_eq "mixed: three failed cases" "3" "$(failed_case_names "${log}" | grep -c .)"
expect_eq "mixed: the summaries count three failures" "3" "$(summary_failed_total "${log}")"
expect_true "mixed: every failure is accounted for" failures_accounted_for "${log}"
expect_eq "mixed: the Fact and the theory method are both re-run (the theory once, not per case)" \
    "FullyQualifiedName=Wondarr.Api.Tests.SignalRTests.Broadcasts_a_queue_change|FullyQualifiedName=Wondarr.Core.Tests.Organizer.NamingTests.Renders_tokens" \
    "$(failed_case_names "${log}" | failed_method_names | rerun_filter)"
expect_eq "mixed: the summaries total all three projects" "2609" "$(summary_total "${log}")"

# --- the old bug: only the Fact is captured, the theory failures are not -> refuse the re-run ---------
only_fact="$(mktemp)"
grep -v 'Renders_tokens' "${log}" > "${only_fact}"
expect_eq "unparsed theory: only the Fact is named" "1" "$(failed_case_names "${only_fact}" | grep -c .)"
expect_false "unparsed theory: the names do not account for the summary, so no re-run" failures_accounted_for "${only_fact}"
rm -f "${only_fact}"

# --- a failure with no name at all -------------------------------------------------------------------
log="${samples}/unaccounted-failure.txt"
expect_false "unnamed failure: one name, two failures, no re-run" failures_accounted_for "${log}"

# --- a flaky failure plus a project that printed no summary at all (its host crashed) ---------------------
log="${samples}/silent-project.txt"
expect_true "silent project: the one failure is accounted for" failures_accounted_for "${log}"
expect_eq "silent project: only two of three projects reported" "2" "$(projects_reported "${log}")"
expect_false "silent project: three summaries are required, so no re-run" test "$(projects_reported "${log}")" -eq 3

# --- a test-host crash after an all-green run: no names, no failures ---------------------------------
log="${samples}/host-plumbing.txt"
expect_eq "plumbing: no failed names" "0" "$(failed_case_names "${log}" | grep -c .)"
expect_eq "plumbing: no failures in the summaries" "0" "$(summary_failed_total "${log}")"
expect_eq "plumbing: three projects passed" "3" "$(projects_passed "${log}")"
expect_false "plumbing: nothing to account for" failures_accounted_for "${log}"

# --- a test host that died mid-run still prints "Passed!" for the few tests it ran (M-50 merge, 2026-10-16) ----
log="${samples}/aborted-mid-run.txt"
expect_eq "aborted mid-run: the cut-short project still prints a Passed! line" "2" "$(projects_passed "${log}")"
expect_true "aborted mid-run: detected" run_aborted_mid_run "${log}"
expect_false "plumbing: a host that crashed after an all-green run is not an abort mid-run" run_aborted_mid_run "${samples}/host-plumbing.txt"
expect_false "silent project: not an abort mid-run either" run_aborted_mid_run "${samples}/silent-project.txt"

# --- node_modules is reinstalled when the lockfile moves (the Dependabot bumps, 2026-10-21) ------------
# A stub npm stands in for the real one: `npm ci` empties node_modules as the real one does, and each call
# is counted. NPM_FAILS=1 makes it fail.
install_dir="$(mktemp -d)"
npm() {
    rm -rf node_modules && mkdir node_modules
    echo "$*" >> "${install_dir}/npm-calls"
    [[ "${NPM_FAILS:-0}" == "0" ]]
}
npm_calls() { grep -c . "${install_dir}/npm-calls" 2>/dev/null || echo 0; }
pushd "${install_dir}" >/dev/null || exit 1
echo '{"lockfileVersion": 3, "packages": {"node_modules/vitest": {"version": "5.0.2"}}}' > package-lock.json
expect_false "install: no node_modules is not current" frontend_install_current
mkdir node_modules
expect_false "install: a node_modules without a stamp is not current (the old check only asked whether the folder existed)" frontend_install_current
expect_true "install: the first run installs" frontend_install
expect_eq "install: npm ci ran once" "1" "$(npm_calls)"
expect_true "install: now current" frontend_install_current
expect_true "install: a second run on the same lockfile" frontend_install
expect_eq "install: and npm ci did not run again" "1" "$(npm_calls)"
echo '{"lockfileVersion": 3, "packages": {"node_modules/vitest": {"version": "5.0.4"}}}' > package-lock.json
expect_false "install: a bumped lockfile is not current" frontend_install_current
expect_true "install: so the next run reinstalls" frontend_install
expect_eq "install: npm ci ran a second time" "2" "$(npm_calls)"
echo '{"lockfileVersion": 3, "packages": {"node_modules/vitest": {"version": "5.0.5"}}}' > package-lock.json
NPM_FAILS=1
expect_false "install: a failed npm ci fails the install" frontend_install
NPM_FAILS=0
expect_false "install: and leaves no stamp claiming the new lockfile" frontend_install_current
rm -f package-lock.json
expect_false "install: a stamp with no lockfile is not current" frontend_install_current
popd >/dev/null || exit 1
unset -f npm npm_calls
rm -rf "${install_dir}"

# --- the project count comes from the solution -------------------------------------------------------
if command -v dotnet >/dev/null 2>&1; then
    root="$(cd "${here}/../.." && pwd)"
    expected="$(cd "${root}" && find tests -maxdepth 2 -name '*.csproj' | wc -l | tr -d ' ')"
    expect_eq "projects: counted from the solution, not written down" "${expected}" "$(cd "${root}" && count_test_projects)"
else
    echo "skip - projects: dotnet is not installed"
fi

# --- the WSL wrapper must not exec, or its cleanup trap never runs (R-094) ---------------------------
if grep -qE '^exec bash .*safe-merge\.sh' "${here}/../wsl-safe-merge.sh"; then
    fail "wsl-safe-merge.sh still execs safe-merge.sh"
else
    pass "wsl-safe-merge.sh does not exec safe-merge.sh"
fi

if [[ "${failures}" -gt 0 ]]; then
    echo "${failures} check(s) failed" >&2
    exit 1
fi
echo "all checks passed"
