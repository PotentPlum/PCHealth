# Decisions log

Each entry: status (**Accepted**, **Provisional**: revisit before it matters, **Open**: undecided), decision, and why. Add entries in the slice that makes the decision; don't rewrite history. Supersede old entries with new ones instead.

## D-001 Language and runtime: all C# on .NET 10 LTS
**Provisional** (2026-10-21)

C# only. Native Windows APIs (MFT/USN, `GetFinalPathNameByHandle`, reparse points, Recycle Bin) are called via P/Invoke. C++ is added only if a benchmark shows C# cannot meet a defined target (spec Â§6.1).

.NET 10 because it is the current LTS. .NET 9 is STS and leaves support in November 2026.

## D-002 UI framework: WPF
**Provisional** (2026-10-21)

WPF is mature, builds with the plain .NET SDK, and needs no extra workloads. WinUI 3 is the alternative if a modern Fluent look becomes a priority. Confirm in T-28 (app shell).

## D-003 Development environment: hybrid
**Accepted** (2026-10-21)

- **Windows host** (`.config/configuration.winget`): builds and tests everything.
- **Linux dev container** (`.devcontainer/`): builds and tests portable `net10.0` projects only.

Why: the host is Windows 11 Home, which cannot run Windows containers, so a dev container can only be Linux. Most safety-critical behavior (path resolution, reparse points, Recycle Bin, MFT/USN) is Windows-only and must be tested on the host. The container still adds value for the portable core, and its tests cannot accidentally touch the host's real files outside the mounted repo.

Consequence: Windows-specific behavior sits behind interfaces in portable projects (see CLAUDE.md layout).

## D-004 Test framework: xUnit v3 on Microsoft.Testing.Platform
**Accepted** (2026-10-21, T-02; was Provisional)

The de facto standard for .NET. Version: `xunit.v3` 4.x (4.0.2 at adoption), pinned in `Directory.Packages.props`. v2 is maintenance-only, so a new codebase starts on v3. xUnit v3 4.x runs only on Microsoft.Testing.Platform (MTP), and the .NET 10 SDK refuses MTP projects under the old VSTest `dotnet test`. So:

- `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`. There is no `Microsoft.NET.Test.Sdk` and no `xunit.runner.visualstudio`.
- Test projects are executables with `UseMicrosoftTestingPlatformRunner`, set once in `tests/Directory.Build.props`.
- `dotnet test` takes `--solution`/`--project`. A bare solution path or `--nologo` gets forwarded to the test apps, and they fail with exit code 5.
- Placeholder test projects with no tests would fail with MTP exit code 8, so `tests/Directory.Build.props` passes `--ignore-exit-code 8`.

Test projects are dev-only and never ship, so they make no runtime network calls in the product.

## D-005 Ledger storage: SQLite via Microsoft.Data.Sqlite
**Provisional** (2026-10-21)

Suggested by the spec (Â§6.4). It is durable, local and inspectable. Confirm in T-20.

## D-006 Full-file hash algorithm and collision policy
**Accepted** (2026-10-21, T-08; was Open)

**Algorithm: SHA-256 from the BCL** (`System.Security.Cryptography.SHA256`/`IncrementalHash`) for the full-file hash and for the partial head+tail digest.

Why not BLAKE3 now:
- The only established .NET package, `Blake3` (xoofx, BSD-2-Clause, 3.0.2 in 2026-07), wraps the Rust implementation through prebuilt native binaries.
- It has a single maintainer and isn't a verified NuGet publisher.
- Shipping third-party native code costs us provenance review, a supply-chain surface and per-RID binaries.
- SHA-256 needs no dependency and uses OS crypto on Windows, which is hardware-accelerated on CPUs with SHA extensions.
- We have no benchmark showing hashing to be the bottleneck rather than disk reads (rule 9).
- Revisit BLAKE3 only if T-37's benchmark shows full hashing is CPU-bound on target hardware. Switching then is a contained change, since stored hashes record their algorithm name.

**Collision and staleness policy:**
1. **Verified duplicates.** A group is "verified duplicates" only when every member has the same size and the same full-file SHA-256. A partial digest or size match never counts as verified.
2. **Hashes are tied to state.** Each hash records the file's size and last-write time when it was hashed. If either has changed, the hash is stale and the file drops back to unverified until rehashed.
3. **Byte comparison before removal.** The execution gateway compares a duplicate byte-for-byte against the copy being kept immediately before removing or quarantining it, as part of revalidation (spec §6.3). A mismatch refuses the action. This also covers a SHA-256 collision, however improbable.
4. **Evidence shows the algorithm.** Evidence names the algorithm next to each hash (e.g. `SHA-256:…`).

## D-008 Claude Code cloud sessions as a fourth environment
**Accepted** (2026-10-21)

Cloud sessions run on Ubuntu 24.04 and, like the dev container, can build and test portable projects only. The .NET SDK isn't pre-installed there. `scripts/cloud/session-start.sh` (a SessionStart hook in `.claude/settings.json`) installs `dotnet-sdk-10.0` from the Ubuntu archive, because the `dotnet-install.sh` download host isn't on the cloud network allowlist. That gives SDK 10.0.1xx, which satisfies `global.json`. Windows-specific code is verified through the CI Windows job (D-009).

Optional speed-up: paste the same install into the cloud environment's setup script. Setup scripts are cached; hooks rerun on every session.

## D-009 Build and delivery pipeline
**Accepted** (2026-10-21)

- **CI** (`.github/workflows/ci.yml`): every push and PR runs the full solution on `windows-latest` and the portable filter on `ubuntu-latest`.
- **Release** (`.github/workflows/release.yml`): a `vX.Y.Z` tag triggers test, self-contained `win-x64` publish, and a Velopack pack (vpk 1.2.161) uploaded to a GitHub Release.

Why Velopack: it gives a one-time install on the other PC, then delta updates pulled from Releases with rollback, and needs no signing certificate (MSIX would). Releases come only from tags, so not every commit reaches the other PC.

Known limits:
- The repo is private, so Windows Actions minutes count double against the free quota.
- The exe is unsigned, so SmartScreen warns on first run.

## D-010 Update channel and update UX
**Partly decided** (2026-10-21)

1. **Channel: public GitHub Releases.** The repo was made public on 2026-10-21, so Velopack can read releases without any token. The earlier options (a token on the other PC, or a shared-folder mirror) are no longer needed.
2. **Update UX: still open.** Updates must be user-visible and user-initiated or confirmed, because an update check is network traffic and the local-first promise requires disclosure (Â§3.5, Â§6.2). Silent auto-install is ruled out for a tool that deletes files. Decide the exact flow in T-35.

## D-012 Public repository hardening
**Accepted** (2026-10-21)

Settings applied when the repo went public:
- **Secret scanning and push protection** are on.
- **Dependabot:** alerts and security updates are on. Weekly version-update PRs cover GitHub Actions and NuGet (`.github/dependabot.yml`). The `global.json` SDK pin is excluded (see that file for why).
- **Private vulnerability reporting** is on.
- **Fork pull requests:** CI runs only after approval for every external contributor, not just first-timers.
- **Default `GITHUB_TOKEN` is read-only.** `release.yml` asks for `contents: write` explicitly.
- **Rulesets:**
  - "Protect main" blocks deletion and force-push.
  - "Protect release tags" (`v*`) blocks deletion, moving and force-push, because a published tag is what the other PC installs.
  - Neither has a bypass. To force-push deliberately, disable the ruleset in repo settings first.
- **Removed the CI run** that still referenced a pre-rewrite commit containing the old author email.

Not done (owner decisions): adding a LICENSE (without one the code is all rights reserved), and requiring PRs or green CI before merging to `main`.

## D-011 Solution format: .slnx
**Accepted** (2026-10-21)

`dotnet new sln` produces `.slnx` by default on .NET 10. CI, the cloud hook and CLAUDE.md all refer to `PCHealth.slnx` and `PCHealth.Portable.slnf`.

## D-013 Task tracking and session protocol
**Accepted** (2026-10-21)

- **Tracking:** `TODO.md` on `main` is the single source of truth for task status (`todo`, `doing`, `done`, `blocked`). Every environment, whether host, container or cloud, reads the same file.
- **Sizing:** each task fits in one session and has an explicit "Done when".
- **Protocol** (CLAUDE.md): one task per session, claimed in its first commit, commits prefixed `T-NN:`, and status set to `done` only once verified.

Why, given the goal of keeping tokens low:
- Status lines are designed to be found with Grep, so sessions read one task block instead of the whole file.
- Tasks cite spec sections and D-entries, so the 32 KB spec is never loaded in full.
- Done tasks shrink to one `Result:` line, so the file doesn't grow with history.
- Git history (`git log --grep T-NN`) replaces session notes.

Phase 2 and 3 are listed but deliberately not sliced. They get sliced once Phase 1 is done.

## D-007 Process boundaries and IPC
**Open**

The spec wants separate permission boundaries for read-only inventory, AI inference and elevated execution (Â§6.1, Â§6.5). Open questions: how many processes, the IPC mechanism (named pipes are likely), and how the elevated helper is launched and authenticated. Phase 1 can run unelevated in one process, as long as the gateway is a clean seam. Decide before any operation that needs elevation.
