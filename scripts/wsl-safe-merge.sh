#!/usr/bin/env bash
# Wrapper for scripts/safe-merge.sh on this box: the default bash is WSL's, where the Windows
# dotnet.exe is not on PATH and a bare `dotnet` does not resolve to `dotnet.exe`. The shim is
# created per run under /tmp so nothing is added to the repo or the system.
# Usage: bash scripts/wsl-safe-merge.sh <branch> <message>
set -euo pipefail

shim_dir="$(mktemp -d)"
trap 'rm -rf "${shim_dir}"' EXIT
printf '#!/usr/bin/env bash\nexec "/mnt/c/Program Files/dotnet/dotnet.exe" "$@"\n' > "${shim_dir}/dotnet"
chmod +x "${shim_dir}/dotnet"
# npm/node: the Windows install is not on WSL's PATH either. npm is a batch file (npm.cmd), so the
# shim runs it through cmd.exe; node.exe itself runs directly.
printf '#!/usr/bin/env bash\nexec "/mnt/c/Windows/System32/cmd.exe" /c "C:\\\\Program Files\\\\nodejs\\\\npm.cmd" "$@"\n' > "${shim_dir}/npm"
chmod +x "${shim_dir}/npm"
printf '#!/usr/bin/env bash\nexec "/mnt/c/Program Files/nodejs/node.exe" "$@"\n' > "${shim_dir}/node"
chmod +x "${shim_dir}/node"
printf '#!/usr/bin/env bash\nexec "/mnt/c/Windows/System32/cmd.exe" /c "C:\\\\Program Files\\\\nodejs\\\\npx.cmd" "$@"\n' > "${shim_dir}/npx"
chmod +x "${shim_dir}/npx"
export PATH="${shim_dir}:$PATH"

# WSL's HOME is not the Windows profile, so the global git identity is not found there. On this
# box the Windows HOME is D:\Code (the global .gitconfig lives there), so point WSL's HOME at it.
export HOME=/mnt/d/Code

exec bash "$(dirname "$0")/safe-merge.sh" "$@"
