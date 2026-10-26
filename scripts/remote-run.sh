#!/usr/bin/env bash
# Runs a build or test command on the test host instead of this machine, in a copy of the working tree as
# git sees it (tracked files and untracked ones that are not ignored, uncommitted edits included), and
# exits with the command's own status. One run at a time: a run that finds the host busy says so and waits
# for the lock, so parallel sessions queue instead of starving each other (2026-10-26: two sessions'
# safe-merge and test runs kept this PC at 100% CPU).
#
#   scripts/remote-run.sh dotnet test --filter FullyQualifiedName~SlskdHostTests
#   scripts/remote-run.sh bash -c 'cd frontend && npm ci && npm test'
#
# The host comes from WONDARR_TEST_HOST (an ssh destination), else from .worker/test-host.env in the main
# checkout, which every worktree shares (git-ignored, so the host stays out of the repository):
#
#   WONDARR_TEST_HOST=you@build-box
#   WONDARR_TEST_SSH_KEY=~/.ssh/id_ed25519
#
# With no host, or WONDARR_TEST_HOST=local, the command runs here, so a script can call this either way.
#
# The host is any Linux machine with ssh, tar, rsync and flock, the .NET SDK that global.json names in
# ~/.dotnet, Node 24 in ~/.local/node, and ~/wondarr-tools/env.sh putting both on PATH (export
# DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$HOME/.local/node/bin:$PATH"). Each checkout gets its own
# folder there (~/wondarr-runs/<name>-<hash>) whose bin/, obj/ and node_modules/ survive between runs, so
# builds stay incremental; every other file is made to match the tree, deletions included. A run whose
# ssh connection drops keeps going there until it ends (the lock waits for it).
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "${root}"

if [[ -z "${WONDARR_TEST_HOST:-}" ]]; then
    common="$(git rev-parse --path-format=absolute --git-common-dir)"
    config="$(dirname "${common}")/.worker/test-host.env"
    if [[ -f "${config}" ]]; then
        # shellcheck source=/dev/null
        . "${config}"
    fi
fi

host="${WONDARR_TEST_HOST:-local}"
if [[ $# -eq 0 ]]; then
    echo "usage: scripts/remote-run.sh <command> [args...]" >&2
    exit 2
fi
if [[ "${host}" == "local" ]]; then
    exec "$@"
fi

ssh_bin=ssh
key="${WONDARR_TEST_SSH_KEY:-}"
if grep -qi microsoft /proc/version 2>/dev/null && [[ -x /mnt/c/Windows/System32/OpenSSH/ssh.exe ]]; then
    # Under WSL (scripts/wsl-safe-merge.sh) a key on the Windows drive has the open permissions WSL's ssh
    # refuses; Windows' own ssh reads it where it is, given a Windows path.
    ssh_bin=/mnt/c/Windows/System32/OpenSSH/ssh.exe
    if [[ "${key}" =~ ^/([a-z])/(.*)$ ]]; then
        key="${BASH_REMATCH[1]^}:/${BASH_REMATCH[2]}"
    elif [[ "${key}" == /mnt/* ]]; then
        key="$(wslpath -w "${key}")"
    fi
fi
ssh_args=(-T -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=30)
if [[ -n "${key}" ]]; then
    ssh_args+=(-i "${key}")
fi

# One folder per checkout: its name, and a hash of its path so two worktrees of the same name never share.
slot="$(basename "${root}")-$(printf '%s' "${root}" | sha1sum | cut -c1-10)"
work="wondarr-runs/${slot}"
stage="wondarr-runs/.stage-${slot}-$$-${RANDOM}"

# The command, quoted for the remote bash exactly as it was given here.
command=""
for argument in "$@"; do
    command+="$(printf '%q' "${argument}") "
done

echo "remote-run: sending the tree to ${host}" >&2
# A file deleted in the working tree is still in the index; only files that exist go.
git ls-files -z --cached --others --exclude-standard \
    | while IFS= read -r -d '' file; do [[ -e "${file}" ]] && printf '%s\0' "${file}"; done \
    | tar --null -T - -czf - \
    | "${ssh_bin}" "${ssh_args[@]}" "${host}" "mkdir -p '${stage}' && tar -xzf - -C '${stage}'"

# Under the lock: make the run folder match the tree (keeping build outputs and installed packages),
# then run. The staging copy goes whatever the command does.
status=0
"${ssh_bin}" "${ssh_args[@]}" "${host}" "bash -s" <<REMOTE || status=$?
set -uo pipefail
trap 'rm -rf "\$HOME/${stage}"' EXIT
mkdir -p "\$HOME/wondarr-tools" "\$HOME/${work}"
exec 9>"\$HOME/wondarr-tools/run.lock"
if ! flock -n 9; then
    echo "remote-run: ${host} is running another check; waiting for it" >&2
    flock 9
fi
rsync -a --delete --exclude=bin/ --exclude=obj/ --exclude=node_modules/ --exclude=TestResults/ \
    "\$HOME/${stage}/" "\$HOME/${work}/"
cd "\$HOME/${work}"
. "\$HOME/wondarr-tools/env.sh"
echo "remote-run: running on ${host}: ${command}" >&2
# Not on this script's stdin: bash -s reads the rest of the script from it.
${command} </dev/null
exit \$?
REMOTE
exit "${status}"
