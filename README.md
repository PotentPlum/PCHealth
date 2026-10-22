# PCHealth

PC Health Monitor is a local-first Windows utility for finding storage waste and "PC drift", reviewing the evidence, and making changes with a clear recovery path. See [docs/spec.md](docs/spec.md).

**Status:** Phase 1, pre-code. Progress is tracked in [TODO.md](TODO.md).

## Working with Claude

The work is split into small tasks in [TODO.md](TODO.md), each with a status, so any environment (this PC, the dev container, or a cloud session) can see what's done. [CLAUDE.md](CLAUDE.md) has the session protocol Claude follows.

Start a session with one short prompt:

| Prompt | What happens |
|-|-|
| `Do the next task` | Claude picks the first `todo` task whose dependencies are done, finishes it, verifies it, commits, and stops. |
| `Do T-12` | Same, for a specific task. |
| `Status` | Claude lists open tasks from TODO.md without reading anything else. |

Keep each session to one task. A fresh session per task is cheaper than one long session, because the context stays small.

Tasks marked **W** involve Windows-specific code. Cloud sessions can write them, but they count as done only once the CI Windows job is green. Tasks marked **U** need you.

## Environments

### Windows host (full build and test)

```powershell
winget configure --enable                           # one-time, may need an admin prompt
winget configure -f .config/configuration.winget
dotnet --list-sdks                                  # expect 10.0.x
```

Equivalent without winget configuration: `winget install --id Microsoft.DotNet.SDK.10 -e`.

### Dev container (portable core only)

Requires Docker Desktop and the VS Code Dev Containers extension. Run "Dev Containers: Reopen in Container".

### Claude Code cloud sessions (portable core only)

One-time setup at [claude.ai/code](https://claude.ai/code):

1. Make sure the Claude GitHub App is installed on this repo.
2. Open the environment selector, then the settings of the environment you'll use (e.g. **Default**).
3. Set **Network access** to **Trusted**. It's the default; it allows NuGet and the Ubuntu package archive.
4. Paste this into **Setup script**:
   ```bash
   apt-get update -qq && apt-get install -y -qq dotnet-sdk-10.0
   ```
   This is optional; a SessionStart hook installs the SDK anyway. The setup script is cached, so sessions start faster.
5. Leave environment variables empty. Never put tokens or secrets there.
6. Test it with a session prompt like `Run dotnet --list-sdks`. Expect `10.0.1xx`.

Linux environments can't build or test Windows-specific code or the UI; CI covers that (D-003, D-008 in [docs/decisions.md](docs/decisions.md)).

## CI and releases

- Every push runs [CI](.github/workflows/ci.yml): the full solution on Windows and the portable projects on Linux.
- There are two ways to publish a release:
  - Push a version tag:
    ```bash
    git tag v0.1.0
    git push origin v0.1.0
    ```
  - In **Actions → Release → Run workflow**, run it on `main` with the version. It refuses unless CI passed on that commit and the tag is new, and it creates the tag itself.

  Either way, [release.yml](.github/workflows/release.yml) runs the tests, builds a self-contained Windows build, packages it with Velopack, and attaches it to a GitHub Release.

## Installing on another PC

1. Download `PCHealth-win-Setup.exe` from the repo's Releases page and run it once. SmartScreen will warn because the exe is unsigned.
2. After that, open **Updates** in the app and choose **Check for updates**. The app never checks on its own. If a newer release exists, **Install and restart** downloads it from this repo's public Releases. No token is needed (D-010 in [docs/decisions.md](docs/decisions.md)).
