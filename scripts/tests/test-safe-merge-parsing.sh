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
log="${samples}/theory-failure.log"
expect_eq "theory: one failed case, arguments kept" \
    'Wondarr.Core.Tests.Organizer.NamingTests.Renders_tokens(template: "{Artist} - {Title}", expected: "A - B")' \
    "$(failed_case_names "${log}")"
expect_eq "theory: the method is cut at the first parenthesis" \
    "Wondarr.Core.Tests.Organizer.NamingTests.Renders_tokens" \
    "$(failed_case_names "${log}" | failed_method_names)"
expect_eq "theory: the summary counts one failure" "1" "$(summary_failed_total "${log}")"
expect_true "theory: every failure is accounted for" failures_accounted_for "${log}"

# --- a nested class keeps its "+" --------------------------------------------------------------------
log="${samples}/nested-class.log"
expect_eq "nested class: the method name keeps Outer+Inner" \
    "Wondarr.Core.Tests.Jobs.CommandQueueTests+Cancelling.A_queued_command_can_be_cancelled" \
    "$(failed_case_names "${log}" | failed_method_names)"
expect_true "nested class: every failure is accounted for" failures_accounted_for "${log}"
expect_eq "nested class: the re-run filter selects it" \
    "FullyQualifiedName=Wondarr.Core.Tests.Jobs.CommandQueueTests+Cancelling.A_queued_command_can_be_cancelled" \
    "$(failed_case_names "${log}" | failed_method_names | rerun_filter)"

# --- a flaky Fact next to a really broken theory: both are named, so both are re-run ------------------
log="${samples}/mixed-flaky-and-real.log"
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
log="${samples}/unaccounted-failure.log"
expect_false "unnamed failure: one name, two failures, no re-run" failures_accounted_for "${log}"

# --- a test-host crash after an all-green run: no names, no failures ---------------------------------
log="${samples}/host-plumbing.log"
expect_eq "plumbing: no failed names" "0" "$(failed_case_names "${log}" | grep -c .)"
expect_eq "plumbing: no failures in the summaries" "0" "$(summary_failed_total "${log}")"
expect_eq "plumbing: three projects passed" "3" "$(projects_passed "${log}")"
expect_false "plumbing: nothing to account for" failures_accounted_for "${log}"

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
