# TODO

This file is the single source of truth for task status. Every environment reads it from `main`. The work process is in [CLAUDE.md](CLAUDE.md#session-protocol).

**Status values:** `todo` · `doing` · `done` · `blocked`

**Env:**
- **P**: portable; can be done and verified in any environment.
- **W**: Windows behavior; can be written anywhere, but is only verified on the Windows host or by the CI Windows job.
- **U**: needs the user.

**Find work without reading the whole file:**

```
grep -nE '^### T-[0-9]+[a-z]? .*`(doing|blocked|todo)`' TODO.md
```

Then read only the block of the chosen task.

**Task format:**

```
### T-NN Title · `status` · Env · deps: T-xx
Refs: spec §… · D-… · area: path
Done when: …
```

- **Starting:** `doing (env, date)`, where env is `host`, `cloud` or `container`.
- **Done:** replace the Refs and Done when lines with one `Result:` line. Details live in `git log --grep "T-NN"`.
- **Blocked:** add a `Blocked:` line with the reason.
- **Too big for one session:** split the task into T-NNa, T-NNb, and so on.

---

## Phase 1: Establish value and trust (spec §8)

### Slice 1: Skeleton

### T-01 Solution and projects · `done` · W · deps: —
Result: `PCHealth.slnx` with 9 empty `src/` projects (6 × `net10.0`, 3 × `net10.0-windows` incl. WPF app), `Directory.Build.props`, central package management; CI Windows job green (run 39631188579).

### T-02 Test projects and portable filter · `done` · W · deps: T-01
Result: xUnit v3 4.0.2 on Microsoft.Testing.Platform (D-004); a test project per `src/` project plus `PCHealth.Testing` (`TempRoot`) with 11 fixture tests; `PCHealth.Portable.slnf`; both CI jobs green (run 39634392911).

### Slice 2: Read-only inventory

### T-03 Core finding model · `done` · P · deps: T-02
Result: `FileEntry`/`DirectoryEntry`, `Finding`, `Evidence` (path, measurement), separate `Confidence`/`Impact`/`Recovery` with no combined score, distinct `MeasuredSize`/`EstimatedSize`; 23 tests in `PCHealth.Core.Tests`.

### T-04 Fallback recursive scanner · `done` · P · deps: T-03
Result: `IInventoryScanner` + `RecursiveScanner` (metadata-only, records but never follows reparse points, includes hidden/system, `InaccessibleEvidence` for unreadable dirs, cancellable, per-directory progress); 12 tests, access-denied case verified as non-root (skips as root).

### T-05 Scanner on Windows · `done` · W · deps: T-04
Result: 6 Windows tests in `PCHealth.Inventory.Windows.Tests` (junctions + cycle, dir symlinks, hidden/system, >260-char paths, fixture-ACL access denied); `TempRoot` now unlinks junctions before deleting; CI Windows green (run 39646389836).

### T-06 Large-folder discovery · `done` · P · deps: T-04
Result: `StorageSummary` rolls measured sizes up per folder (links add nothing, incomplete scans flagged) and ranks top-N folders and `FileCategory` totals; 13 tests.

### T-07 Low-impact scanning · `done` · P · deps: T-04
Result: `BackgroundScan` runs on a below-normal-priority background thread (CPU only; I/O priority is a later Windows concern) with pause/resume/cancel via `PauseToken`; `ScanStatistics` and progress report counts, elapsed and paused time; 6 tests.

### Slice 3: Duplicates

### T-08 Decide hash algorithm · `done` · P · deps: —
Result: D-006 accepted: BCL SHA-256 for partial and full hashes (BLAKE3 deferred to T-37 benchmarks); verified = same size + same full hash, hashes tied to size+mtime, byte-for-byte comparison in the gateway before any removal.

### T-09 Candidate stages · `done` · P · deps: T-04, T-08
Result: `CandidateFinder`: size groups (zero-byte, links and repeated paths skipped), then SHA-256 head+tail digest (64 KiB per end); unreadable files become `InaccessibleEvidence`, files changed since the scan (size/mtime) are dropped; 10 tests incl. same-size different-content.

### T-10 Verified duplicate groups · `done` · P · deps: T-09
Result: `DuplicateVerifier` full-hashes candidates with SHA-256 (size+mtime rechecked before and after); `VerifiedDuplicateGroup` (only the verifier can create one) carries `FileContentEvidence` (path, size, mtime, hash) and Verified confidence; `DuplicateFinder` runs the whole pipeline; 8 tests.

### T-11 Same-file detection · `done` · W · deps: T-10
Result: `FileIdentity`/`IFileIdentityProvider` (Core); verified groups need two or more distinct physical files and expose `PhysicalCopies`; `WindowsFileIdentityProvider` reads `FILE_ID_INFO` by handle; 3 portable + 3 Windows hard-link tests; CI Windows green (run 39653855265).

### T-12 Version candidates · `done` · P · deps: T-10
Result: `VersionCandidates` groups same-extension files whose names match once copy/version/date markers are stripped; `VersionCandidateGroup` is separate from verified duplicates, always `ReviewOnly`, confidence Medium (same folder) or Low; 23 tests.

### Slice 4: Policy and protection

### T-13 Path canonicalization · `done` · W · deps: T-02
Result: `IPathResolver`/`PathResolution` (Execution) and `WindowsPathResolver` (syntax rejection, then `CreateFileW` without following the last link + `GetFinalPathNameByHandleW`); 38 adversarial Windows tests (`..`, case, 8.3, `\\?\`, UNC, ADS, device names, trailing dot/space, long paths); fails closed; CI Windows green (run 39654961964).

### T-14 Reparse-point escape detection · `done` · W · deps: T-13
Result: `ScopeValidator` (Execution) rejects targets whose final path leaves the scope and links whose destination is outside or unresolvable; resolver gained `LinkHandling.Follow`; 10 portable + 9 Windows tests with fixture junctions, dir/file symlinks, a dangling link and a volume-GUID (mount-point form) junction; CI Windows green (run 39655668565).

### T-15 Protected locations · `done` · W · deps: T-13
Result: `ProtectionPolicy` (Policy) blocks OS, Program Files, boot/recovery, personal folders (D-014), folders containing them, drive roots and program binaries, each with a readable reason; `WindowsProtectedLocations` discovers them via `SHGetKnownFolderPath`; 10 portable + 12 Windows tests; CI Windows green (run 39656523422).

### T-16 User exclusions · `done` · P · deps: T-03
Result: `ExclusionList` (file, folder-and-below, `FindingCategory`) saved atomically as local JSON (System.Text.Json, no new dependency), corrupt files rejected; `IsExcluded` for builders and `Apply` withholds findings by category or any evidence path, with the matching exclusion; `Finding` gained `Category`; 16 tests.

### T-17 Known-cache catalog · `done` · W · deps: T-06, T-15
Result: embedded `known-caches.json` (10 rules, each with rebuild/download text) evaluated by `CacheCatalog` (token locations with one `*` segment, protection + exclusion checks, read-only scan) into `Cache` findings with path/measured-size evidence; `WindowsKnownLocations` via known folders; 13 fixture tests + 7 Windows tests; CI Windows green (run 39657131868).

### T-18 Orphaned app-data detection · `done` · W · deps: T-17
Result: `ResidueDetector` cross-references top-level AppData/ProgramData folders and `LocalAppData\Packages` data folders with uninstall entries and package families (`WindowsAppRegistrations`, registry, D-015); missing sources, similar names, unreadable parts, program files or recent writes give a Low, `ReviewOnly` finding; `Finding` gained `ReviewOnly`, plus `RegistrationEvidence`/`CheckEvidence`; 18 fake-registration tests + 3 Windows tests; CI Windows green (run 39659085031).

### T-19 Recommendation builder · `doing (cloud, 2026-10-21)` · P · deps: T-10, T-16
Refs: §3.3, §7 · area: `src/PCHealth.Policy`
Done when:
- Findings become recommendations that answer the six §7 questions, with separate confidence, impact and recovery.
- Uncertain items go to review.
- Tests cover it.

### Slice 5: Ledger and quarantine

### T-20 Ledger · `todo` · P · deps: T-03
Refs: §6.4 · D-005 · area: `src/PCHealth.Ledger`
Done when:
- D-005 is confirmed.
- The SQLite ledger records every field listed in §6.4 and survives restarts.
- Tests run on a temp database.

### T-21 Quarantine store · `todo` · P · deps: T-20
Refs: §6.4 · area: `src/PCHealth.Ledger`
Done when:
- The quarantine layout stores per-item metadata.
- Retention defaults to 14 days and is configurable.
- Expiry purges only items past their deadline and records each purge.
- Tests use a controlled clock.

### T-22 Restore and interrupted operations · `todo` · P · deps: T-21
Refs: §6.4, §9 · area: `src/PCHealth.Ledger`
Done when:
- Operations move through the states pending → applied → verified/failed, so an interrupted operation is detected and resolved at startup.
- Restore works per operation type, with honest limits.
- Tests cover success, partial failure and interruption.

### Slice 6: Execution gateway

### T-23 Gateway pipeline · `todo` · P · deps: T-19, T-22
Refs: §6.3 · CLAUDE.md rules 2–4 · area: `src/PCHealth.Execution`
Done when:
- One gateway runs: validate → protected/exclusion checks → revalidate → execute → verify → ledger.
- It fails closed at every step.
- The global Pause flag blocks all execution.
- Tests use a fake platform layer.

### T-24 Windows quarantine move · `todo` · W · deps: T-14, T-15, T-23
Refs: §6.3 · area: `src/PCHealth.Execution.Windows`
Done when:
- Files move into quarantine by handle: opened without following reparse points, final path checked, renamed by handle.
- A TOCTOU test swaps the target for a junction between check and act, and the move is refused.

### T-25 Recycle Bin deletion · `todo` · W · deps: T-23
Refs: §5.1, §6.4 · area: `src/PCHealth.Execution.Windows`
Done when:
- Suitable items can go to the Recycle Bin, with a ledger entry.
- Fixture-file tests cover it.

### T-26 In-use checks · `todo` · W · deps: T-23
Refs: §6.3 · area: `src/PCHealth.Execution.Windows`
Done when:
- Restart Manager and process checks block changes to files held open by running processes.
- A test holds a file open and confirms the block.

### T-27 Undo end to end · `todo` · W · deps: T-24, T-25
Refs: §3.6, §9 · area: `src/PCHealth.Execution.Windows`, `src/PCHealth.Ledger`
Done when:
- Undo restores a quarantined item to its original path with its metadata.
- A conflict at the original path fails safely.
- Interrupted-operation recovery is verified on Windows.

### Slice 7: UI shell (WPF)

### T-28 App shell · `todo` · W · deps: T-02
Refs: D-002 · area: `src/PCHealth.App`
Done when:
- D-002 is confirmed.
- The WPF shell has navigation and dependency injection.
- The global Pause control is bound to the gateway's Pause state.
- View-model tests pass.

### T-29 First run · `todo` · W · deps: T-28, T-06
Refs: §5.1 (first-run value), §3.5 · area: `src/PCHealth.App`
Done when:
- First run explains what is inspected, what is stored, and that nothing leaves the PC.
- It starts read-only discovery and shows one useful finding quickly.

### T-30 Overview and details · `todo` · W · deps: T-29, T-19
Refs: §3.1, §7 · area: `src/PCHealth.App`
Done when:
- The overview shows a few prioritized findings, each answering the six §7 questions.
- Advanced details are available on demand.

### T-31 Preview and approve · `todo` · W · deps: T-30, T-23
Refs: §4, §6.5 · area: `src/PCHealth.App`
Done when:
- Before approval, the user sees the exact items, exclusions, expected effect, and recovery/retention terms.
- The user can adjust the selection or decline.
- A declined recommendation doesn't come back unless there's new evidence.

### T-32 Activity history and Undo · `todo` · W · deps: T-31, T-27
Refs: §3.6, §6.4 · area: `src/PCHealth.App`
Done when:
- The user can read what changed, why, and with what outcome.
- Undo is offered where supported, and limits and deadlines are shown honestly.

### T-33 Storage detail view · `todo` · W · deps: T-30
Refs: §5.1 · area: `src/PCHealth.App`
Done when:
- There is a large-folders view with drill-down.
- Measured and estimated figures are labeled distinctly.
- Treemap and other visualizations are optional extras, not required.

### T-34 Settings and local data · `todo` · W · deps: T-28
Refs: §6.5 · area: `src/PCHealth.App`
Done when:
- Settings cover the retention window and exclusions.
- The user can inspect and clear the product's locally stored data.

### Slice 8: Delivery and Phase 1 exit

### T-35 Update flow · `todo` · W · deps: T-28
Refs: §3.5 · D-009, D-010 · area: `src/PCHealth.App`
Done when:
- The Velopack library is integrated, at the same version as `VPK_VERSION`.
- "Check for updates" is user-initiated and disclosed.
- The D-010 UX question is decided.

### T-36 Privacy check · `todo` · W · deps: T-35
Refs: §3.5, §9
Done when:
- The app's observed network behavior matches what it discloses: only the update check, only when the user starts it.
- The result is recorded in `docs/`.

### T-37 Performance baseline · `todo` · W · deps: T-07
Refs: §6.1, §9
Done when:
- Scan time, CPU and RAM are measured on the host for a representative fixture tree.
- Results are recorded in `docs/benchmarks.md` as measurements, not guarantees.

### T-38 First release · `todo` · U · deps: T-32, T-35, T-36
Refs: D-009 · CLAUDE.md "CI and releases"
Done when:
- The user asks for v0.1.0.
- The tag is pushed from a green `main`.
- The app is installed on the second PC.
- The update path is proven with v0.1.1.

---

## Later phases (not sliced yet)

Slice these into tasks only after Phase 1 is done. Until then they're out of scope.

- **Phase 2** (§8):
  - MFT/USN inventory
  - file triage and project grouping
  - dependency-aware move previews
  - optional compression
  - more application-specific caches
  - startup/background resource analysis
  - diagnostic timelines
- **Phase 3** (§8):
  - developer artifacts
  - WSL2/Docker virtual disks
  - creative caches and local AI model inventory
  - advanced diagnostics
- **Deferred** (§8): see the spec. Never start these without an explicit decision.
