# PCHealth

PC Health Monitor is a local-first Windows utility for finding storage waste and "PC drift", reviewing the evidence, and making changes with a clear recovery path. See [docs/spec.md](docs/spec.md).

Status: Phase 1, pre-code. See [CLAUDE.md](CLAUDE.md) for the plan and project rules.

## Development environments

**Windows host (full build and test):**

```powershell
winget configure --enable                           # one-time, may need an admin prompt
winget configure -f .config/configuration.winget
dotnet --list-sdks                                  # expect 10.0.x
```

Equivalent without winget configuration: `winget install --id Microsoft.DotNet.SDK.10 -e`.

**Dev container (portable core only):** requires Docker Desktop and the VS Code Dev Containers extension. Run "Dev Containers: Reopen in Container".

**Claude Code cloud sessions (portable core only):**

1. Give cloud sessions push access to this repo: install the [Claude GitHub App](https://github.com/apps/claude) on it, or run `/web-setup` in a local Claude Code session.
2. Use an environment with **Trusted** network access (the default).
3. A SessionStart hook installs the .NET 10 SDK automatically. To make sessions start faster, also put this in the environment's **Setup script** field; setup scripts are cached:
   ```bash
   apt-get update -qq && apt-get install -y -qq dotnet-sdk-10.0
   ```

Linux environments can't build or test Windows-specific code or the UI. CI covers that on every push. Why is explained in [docs/decisions.md](docs/decisions.md) (D-003, D-008).

## CI and releases

- Every push runs [CI](.github/workflows/ci.yml): the full solution on Windows and the portable projects on Linux.
- Pushing a version tag publishes a release:
  ```bash
  git tag v0.1.0
  git push origin v0.1.0
  ```
  [release.yml](.github/workflows/release.yml) runs the tests, builds a self-contained Windows build, packages it with Velopack, and attaches it to a GitHub Release.

## Installing on another PC

Not usable yet; there's nothing to release. Once the first release exists:

1. Download `PCHealth-win-Setup.exe` from the repo's Releases page and run it once. SmartScreen will warn because the exe is unsigned.
2. After that, the app checks for and installs new releases from this repo's public Releases, with your confirmation. No token is needed (D-010 in [docs/decisions.md](docs/decisions.md)).
