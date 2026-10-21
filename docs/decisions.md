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

## D-007 Process boundaries and IPC
**Open**

The spec wants separate permission boundaries for read-only inventory, AI inference and elevated execution (§6.1, §6.5). Open questions: how many processes, the IPC mechanism (named pipes are likely), and how the elevated helper is launched and authenticated. Phase 1 can run unelevated in one process, as long as the gateway is a clean seam. Decide before any operation that needs elevation.
