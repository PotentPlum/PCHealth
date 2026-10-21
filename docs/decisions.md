# Decisions log

Each entry: status (**Accepted**, **Provisional**: revisit before it matters, **Open**: undecided), decision, and why. Add entries in the slice that makes the decision; don't rewrite history. Supersede old entries with new ones instead.

## D-001 Language and runtime: all C# on .NET 10 LTS
**Provisional** (2026-10-21)

C# only. Native Windows APIs (MFT/USN, `GetFinalPathNameByHandle`, reparse points, Recycle Bin) are called via P/Invoke. C++ is added only if a benchmark shows C# cannot meet a defined target (spec §6.1).

.NET 10 because it is the current LTS. .NET 9 is STS and leaves support in November 2026.

## D-002 UI framework: WPF
**Provisional** (2026-10-21)

WPF is mature, builds with the plain .NET SDK, and needs no extra workloads. WinUI 3 is the alternative if a modern Fluent look becomes a priority. Revisit before slice 7 (UI shell).

## D-003 Development environment: hybrid
**Accepted** (2026-10-21)

- **Windows host** (`.config/configuration.winget`): builds and tests everything.
- **Linux dev container** (`.devcontainer/`): builds and tests portable `net10.0` projects only.

Why: the host is Windows 11 Home, which cannot run Windows containers, so a dev container can only be Linux. Most safety-critical behavior (path resolution, reparse points, Recycle Bin, MFT/USN) is Windows-only and must be tested on the host. The container still adds value for the portable core, and its tests cannot accidentally touch the host's real files outside the mounted repo.

Consequence: Windows-specific behavior sits behind interfaces in portable projects (see CLAUDE.md layout).

## D-004 Test framework: xUnit
**Provisional** (2026-10-21)

The de facto standard for .NET. Confirm the exact version (v2 vs v3) in slice 1.

## D-005 Ledger storage: SQLite via Microsoft.Data.Sqlite
**Provisional** (2026-10-21)

Suggested by the spec (§6.4). It is durable, local and inspectable. Confirm in slice 5.

## D-006 Full-file hash algorithm
**Open**

BLAKE3 is the spec's candidate (§5.1), but it needs a third-party NuGet package, which must be evaluated for maintenance and provenance. SHA-256 is built into the BCL with hardware acceleration and would be the fallback. A collision-handling policy is also needed, e.g. byte-for-byte comparison before any delete. Decide in slice 3.

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
**Open**

Two questions:
1. **How the other PC reads releases of a private repo:**
   - (a) A fine-grained, read-only GitHub token stored on that PC. It must never be embedded in the binary.
   - (b) Make the repo public.
   - (c) Mirror releases to a shared folder (e.g. OneDrive), which Velopack can read as a local source.
2. **Update UX:** must be user-visible and user-initiated or confirmed, because an update check is network traffic and the local-first promise requires disclosure (§3.5, §6.2). Silent auto-install is ruled out for a tool that deletes files.

Decide in slice 8.

## D-011 Solution format: .slnx
**Accepted** (2026-10-21)

`dotnet new sln` produces `.slnx` by default on .NET 10. CI, the cloud hook and CLAUDE.md all refer to `PCHealth.slnx` and `PCHealth.Portable.slnf`.

## D-007 Process boundaries and IPC
**Open**

The spec wants separate permission boundaries for read-only inventory, AI inference and elevated execution (§6.1, §6.5). Open questions: how many processes, the IPC mechanism (named pipes are likely), and how the elevated helper is launched and authenticated. Phase 1 can run unelevated in one process, as long as the gateway is a clean seam. Decide before any operation that needs elevation.
