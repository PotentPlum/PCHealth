# PC Health Monitor

Local-first Windows utility that finds what wastes storage and system resources, shows the evidence, and lets the user choose what to change, with a recovery path wherever one can honestly be supported.

**Where things are:**
- [TODO.md](TODO.md): tasks and their status. This is the source of truth for what's done.
- [docs/spec.md](docs/spec.md): the product spec. `§` references point at its sections.
- [docs/decisions.md](docs/decisions.md): decisions `D-NNN`.

## Session protocol

One session does one task. Follow these steps in order:

1. **Sync.** On the host, run `git pull --ff-only`. Cloud sessions start from a fresh clone.
2. **Pick a task.**
   - If the user named one, take it.
   - Otherwise run `grep -nE '^### T-[0-9]+[a-z]? .*`(doing|blocked|todo)`' TODO.md`.
   - Resume a `doing` task first. If there is none, take the first `todo` whose deps are all `done`.
   - Skip `U` tasks; they need the user.
3. **Load only what the task needs.**
   - The task's block in TODO.md: read it by line range, not the whole file.
   - The cited spec sections, found with Grep on the heading (e.g. `^#### Verified duplicate`).
   - The cited D-entries.
   - Files in the task's area.
4. **Claim it.** Set the status to `doing (env, YYYY-MM-DD)` in your first commit for the task. `env` is `host`, `cloud` or `container`.
5. **Build exactly what "Done when" asks.** Nothing more.
   - New decisions go in docs/decisions.md as part of this task.
   - If the task turns out too big, split it in TODO.md (T-NNa, T-NNb), finish the first part, and leave the rest `todo`.
6. **Verify.**
   - Run the touched test project first, then the portable filter (Linux) or the full solution (host).
   - **W** tasks also need the CI Windows job green. See "Verifying Windows code from Linux" below.
7. **Finish.**
   - Set the status to `done`. Replace the task's Refs and Done when lines with one `Result:` line.
   - Commit with the message prefix `T-NN:`, push, and check CI.
   - If you can't finish, leave the status `doing` and add a `Next:` line, or set `blocked` with a `Blocked:` line.
8. **Report and stop.** Give at most 5 lines: what changed, where it was verified, and anything the user must decide. Don't start the next task unless asked.

### Keeping sessions cheap

- Never read docs/spec.md, TODO.md or decisions.md in full. Grep, then read line ranges.
- Don't re-read files already in context. Don't do exploratory sweeps of the repo. Don't spawn subagents unless the user asks.
- Keep builds quiet: `dotnet build -v q --nologo` and `dotnet test --no-build -v q --nologo`. On failure, rerun only the failing project with more detail.
- Status lives in TODO.md, and history lives in `git log --grep "T-NN"`. Don't write session notes or summaries into files.

## Environments (D-003, D-008)

| Where | What builds/tests there | Setup |
|-|-|-|
| Windows host | Everything | README.md |
| GitHub Actions `windows-latest` | Everything, on every push | none |
| Dev container (Linux) | Portable projects only (`net10.0`) | README.md |
| Claude Code cloud (Ubuntu 24.04) | Portable projects only (`net10.0`) | SessionStart hook installs the SDK |

- On Linux, build and test only `PCHealth.Portable.slnf`.
- Windows code (`*.Windows`, `net10.0-windows`) counts as verified only on the host or in the CI Windows job. Always say which environment you verified in.
- **Verifying Windows code from Linux:** push the branch, then run `gh run list --branch <branch>` and `gh run view <id> --log-failed`. If the cloud proxy rejects a `gh` subcommand, use `gh api repos/PotentPlum/PCHealth/actions/runs?branch=<branch>` instead.

## Commands (once the solution exists)

```
dotnet build PCHealth.slnx -v q --nologo                 # host: full build
dotnet test PCHealth.slnx -v q --nologo                  # host: all tests
dotnet test PCHealth.Portable.slnf -v q --nologo         # any environment: portable tests
```

## Layout

This mirrors spec §10. Portable projects never reference Windows APIs. Windows behavior sits behind interfaces declared in portable projects and is implemented in `*.Windows` projects.

```
src/
  PCHealth.Core                 net10.0          findings, evidence, confidence/impact/recovery, proposals
  PCHealth.Inventory            net10.0          inventory abstractions + fallback recursive scanner
  PCHealth.Inventory.Windows    net10.0-windows  MFT/USN inventory (Phase 2)
  PCHealth.Duplicates           net10.0          size -> partial digest -> full hash pipeline
  PCHealth.Policy               net10.0          exclusions, protected locations, recommendation rules
  PCHealth.Ledger               net10.0          transaction ledger (SQLite), quarantine, Undo logic
  PCHealth.Execution            net10.0          execution gateway contract + validation pipeline
  PCHealth.Execution.Windows    net10.0-windows  handle-based paths, reparse checks, Recycle Bin, quarantine moves
  PCHealth.App                  net10.0-windows  WPF UI (D-002)
tests/
  <Project>.Tests per src project, plus PCHealth.Testing (shared temp-root fixture)
```

## Non-negotiable product rules

1. **Discovery is read-only** (§3.4, §6.5).
2. **One mutation path.** Every file or system change goes through the execution gateway (§6.3).
3. **Fail closed.** If a path, its ownership or its expected state can't be validated, refuse (§6.3).
4. **Revalidate right before acting.** Resolve the final path by handle, reject reparse-point escapes, and re-check scope and protected locations (§6.3).
5. **AI proposes, never decides.** AI output is a typed proposal, validated by policy and approved by the user (§3.2, §6.2).
6. **Verified duplicates need full content verification** (§5.1).
7. **Confidence, impact and recovery stay separate.** Never combine them into one score (§3.3).
8. **Honest recovery.** Every mutation gets a ledger transaction. Nothing is permanently deleted while the user could believe it's still recoverable (§3.6, §6.4).
9. **Measured is not estimated.** Make no performance claims before benchmarks exist (§5.1, §6.1).
10. **No unverified privacy claims.** Product code makes no network calls without a decision. Record every new dependency in decisions.md (§3.5, §6.2).

## Development safety rules

- Tests touch only directories they create under the system temp directory, through the `PCHealth.Testing` fixture. Never touch real user folders.
- Never run mutating features against real user paths. Use generated fixture trees.
- Never request elevation in tests. Tests that need admin rights are skipped unless explicitly enabled.
- Never add bypasses of the gateway's validation, not even temporarily.

## CI, releases and the public repo (D-009, D-012)

- `ci.yml` runs on every push and PR: Windows covers the full solution, Linux covers the portable filter.
- `release.yml` runs on `vX.Y.Z` tags: test, publish, Velopack pack, then a GitHub Release.
- **A version tag ships software to the user's other PC.** Never create or push a tag unless the user asks for that specific release, and only from a green `main`.
- The Velopack NuGet version must match `VPK_VERSION` in `release.yml`.
- **The repo is public.** Never commit secrets, tokens, personal data, or real paths from the user's machines. Rulesets block force-pushes to `main` and changes to `v*` tags.
