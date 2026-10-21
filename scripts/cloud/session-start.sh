#!/bin/bash
# SessionStart hook (wired in .claude/settings.json).
# Provisions the .NET 10 SDK in Claude Code cloud sessions (Ubuntu 24.04) and restores
# the portable projects. Does nothing locally: the Windows host and the dev container
# have their own setup (README.md).
#
# Ubuntu's own archive ships dotnet-sdk-10.0. The host that dotnet-install.sh downloads
# from is not on the cloud network allowlist, so apt is used instead.
# Always exits 0 so a failed install never blocks the session; failures are printed.

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
  SUDO="sudo"
fi

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "Installing .NET 10 SDK from the Ubuntu archive..."
  # Hook stdout lands in Claude's context, so keep apt quiet; errors still go to stderr.
  $SUDO apt-get update -qq >/dev/null \
    && $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 >/dev/null \
    || echo "warning: .NET 10 SDK install failed; dotnet commands will not work" >&2
fi

cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0
if [ -f PCHealth.Portable.slnf ]; then
  dotnet restore PCHealth.Portable.slnf || echo "warning: dotnet restore failed" >&2
fi

exit 0
