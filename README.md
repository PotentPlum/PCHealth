# PCHealth

PC Health Monitor is a local-first Windows utility for finding storage waste and "PC drift", reviewing the evidence, and making changes with a clear recovery path. See [docs/spec.md](docs/spec.md).

Status: Phase 1, pre-code. See [CLAUDE.md](CLAUDE.md) for the plan and project rules.

## Setup

**Windows host (full build and test):**

```powershell
winget configure --enable                           # one-time, may need an admin prompt
winget configure -f .config/configuration.winget
dotnet --list-sdks                                  # expect 10.0.x
```

Equivalent without winget configuration: `winget install --id Microsoft.DotNet.SDK.10 -e`.

**Dev container (portable core only):** requires Docker Desktop and the VS Code Dev Containers extension. Run "Dev Containers: Reopen in Container".

The container can't build or test Windows-specific code or the UI. Why is explained in [docs/decisions.md](docs/decisions.md) (D-003).
