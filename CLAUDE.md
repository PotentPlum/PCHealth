# PC Health Monitor

Local-first Windows utility that finds what wastes storage and system resources, shows the evidence, and lets the user choose what to change — with a recovery path wherever one can honestly be supported.

- Full product/technical spec: [docs/spec.md](docs/spec.md) (section numbers below refer to it)
- Decisions and open questions: [docs/decisions.md](docs/decisions.md)

## Current status

**Phase 1 (spec §8), no code yet.** Next step is the solution skeleton (slice 1 below).

Out of scope until explicitly reopened: automatic file organization, registry cleaning, Dormant Mode, driver remediation, symlink/junction redirection, unattended destructive actions, anything from Phase 2/3.

## Environments (D-003)

| Where | What builds/tests there | Setup |
|-|-|-|
| Windows host | Everything (portable + Windows-specific + UI) | `winget configure -f .config/configuration.winget` |
| Dev container (Linux) | Portable projects only (`net10.0`) | VS Code: "Reopen in Container" |

- In the container, build/test only the portable solution filter: `dotnet test PCHealth.Portable.slnf`.
- Windows-specific code (`*.Windows` projects, `net10.0-windows`) is only verified when its tests pass **on the Windows host**. When reporting results, say which environment the tests ran in. Never describe Windows code as verified based on a container run.

## Commands (once the solution exists)

```
dotnet build PCHealth.sln                 # host: full build
dotnet test PCHealth.sln                  # host: all tests
dotnet test PCHealth.Portable.slnf        # container or host: portable tests only
```

## Planned layout

Mirrors spec §10. Portable projects must not reference Windows APIs; Windows behavior sits behind interfaces declared in portable projects and is implemented in `*.Windows` projects.

```
src/
  PCHealth.Core                 net10.0          shared models: findings, evidence, confidence/impact/recovery, proposals
  PCHealth.Inventory            net10.0          inventory abstractions + fallback recursive scanner
  PCHealth.Inventory.Windows    net10.0-windows  MFT/USN inventory (later in Phase 1 or Phase 2)
  PCHealth.Duplicates           net10.0          size -> partial digest -> full hash pipeline
  PCHealth.Policy               net10.0          exclusions, protected locations, recommendation rules
  PCHealth.Ledger               net10.0          transaction ledger (SQLite), quarantine records, Undo logic
  PCHealth.Execution            net10.0          execution gateway contract + validation pipeline
  PCHealth.Execution.Windows    net10.0-windows  handle-based path resolution, reparse checks, Recycle Bin, quarantine moves
  PCHealth.App                  net10.0-windows  WPF UI (provisional, D-002)
tests/
  <Project>.Tests for each src project; adversarial path cases live in PCHealth.Execution.Windows.Tests
```

## Non-negotiable product rules

These come from the spec and override convenience:

1. **Discovery is read-only.** Inventory, duplicate detection and analysis never modify files (§3.4, §6.5).
2. **One mutation path.** Every file/system change goes through the execution gateway. No other component deletes, moves, renames or compresses anything (§6.3).
3. **Fail closed.** If a target path, its ownership or its expected state cannot be validated, the operation is refused (§6.3).
4. **Revalidate right before acting.** Resolve the final path via handle, reject reparse-point/junction escapes, re-check scope and protected locations immediately before modification (§6.3, TOCTOU).
5. **AI proposes, never decides.** AI output is a typed, schema-validated proposal with no write/execute authority, and it is always validated by deterministic policy plus user approval (§3.2, §6.2).
6. **Verified duplicates need full content verification.** Similar names, sizes or versions are never enough to call something a duplicate (§5.1).
7. **Confidence, impact and recovery stay separate.** Never collapse them into a single "safe" score (§3.3).
8. **Honest recovery.** Every mutating operation records a ledger transaction, including what can and cannot be restored. Nothing is permanently deleted while the user could reasonably believe it is still recoverable (§3.6, §6.4).
9. **Measured vs estimated.** "Potentially reclaimable" is never shown as reclaimed. Performance targets are not claimed until benchmarked (§5.1, §6.1).
10. **No unverified privacy claims.** Product code makes no network calls without a decision entry. New NuGet dependencies are recorded in docs/decisions.md because they affect the local-first claim (§3.5, §6.2).

## Development safety rules

This tool deletes and moves real files, so development must not:

- Let tests touch anything except directories they create themselves under the system temp directory (and delete afterwards). No test may reference the real user profile, Desktop, Downloads, Documents, AppData, ProgramData, or any path outside its own fixture root.
- Run mutating features of the app against real user paths during development or manual verification. Use a generated fixture tree.
- Request elevation in tests. Tests that need admin rights are skipped unless explicitly enabled, and are never run without asking.
- Add `--force` style bypasses of the gateway's validation, even "temporarily for testing".

## Way of working

- Build in thin vertical slices. Each slice includes tests and is committed only when its tests pass.
- Record decisions (and newly discovered open questions) in docs/decisions.md as part of the slice that made them.
- Prefer the BCL and P/Invoke over new dependencies.

### Phase 1 slice order

1. Solution skeleton: projects above, `PCHealth.Portable.slnf`, test projects, build green in both environments
2. Read-only inventory using the fallback recursive scanner, with large-folder discovery
3. Duplicate pipeline (size -> partial digest -> full hash), version candidates kept separate
4. Policy: exclusions and protected locations, plus Windows path canonicalization with adversarial tests
5. Ledger and quarantine (SQLite), with retention window and expiry behavior
6. First mutating action through the gateway (quarantine/Recycle Bin), plus Undo, plus interrupted/partial-failure tests
7. UI shell: overview with one finding, preview, approve, activity history, global Pause
