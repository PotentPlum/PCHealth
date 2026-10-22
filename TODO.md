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

### T-19 Recommendation builder · `done` · P · deps: T-10, T-16
Result: `RecommendationBuilder` withholds excluded findings, strips protected/excluded `ProposedOperation`s (new Core type; findings now carry typed operations that must match their recovery) and answers the six §7 questions per `Recommendation` with confidence/impact/recovery kept separate; review-only, Low-confidence, informative or fully blocked findings get `Disposition.Review` with a reason; `FindingSources` turns verified duplicates (keeps protected, then oldest copy), version groups and large folders into findings; 11 + 2 tests.

### Slice 5: Ledger and quarantine

### T-20 Ledger · `done` · P · deps: T-03
Result: D-005 accepted (`Microsoft.Data.Sqlite` 10.0.13); `TransactionLedger` stores `LedgerTransaction`s with every §6.4 field (id, time, operation, state/outcome, reason, checks, items with before/after metadata, recovery location and per-item status/error, expected/observed bytes, recovery method/limitations/deadline) in WAL + synchronous=FULL SQLite; terms can't be rewritten, newer or non-ledger files are refused; 7 temp-database tests incl. reopen.

### T-21 Quarantine store · `done` · P · deps: T-20
Result: `QuarantineStore` (root per D-016) gives each item a slot `<transaction>/<position>/` with `item.json` (original path, before-metadata, quarantined time, deadline, Prepared/Stored) beside `content`; retention defaults to 14 days and is configurable; `PurgeExpired` deletes only stored items past the later of slot and ledger deadline, never follows links, and records each purge as a `PurgeQuarantine` ledger transaction (pending first, then per-item outcome) linked by `RelatesTo`; ledger gained `LedgerOperation`; 8 + 1 tests on `ManualClock`.

### T-22 Restore and interrupted operations · `done` · P · deps: T-21
Result: the ledger enforces pending → applied → verified/failed; `InterruptedOperations.Resolve` (startup) settles pending/applied transactions per type from what is on disk (moved, not moved, both, neither), releases empty slots and reports unexplained slots without deleting them; `Restorer` restores quarantine moves through an `IFileMover` (conflict at the original path → item stays in quarantine), re-applies timestamps, records each restore as its own transaction, and states honest limits for Recycle Bin, purge and restore records; 13 tests incl. a simulated crash mid-restore.

### Slice 6: Execution gateway

### T-23 Gateway pipeline · `done` · P · deps: T-19, T-22
Result: `ExecutionGateway` is the one mutation path: validates the request (Act disposition, approved ⊆ recommendation, one kind matching recovery) → protection/exclusion checks → revalidates per item right before acting (final path by handle and unchanged, no links, scope, protection again, size/mtime, open handles, elevation per D-019, same volume per D-016) → executes through `IPlatformOperations` → verifies → ledger (pending first, progress per item, applied, verified/failed); `GlobalPause` (optionally persisted) blocks all and stops queued items; 17 tests on a fake platform and resolver incl. mid-run pause and crash.

### T-24 Windows quarantine move · `done` · W · deps: T-14, T-15, T-23
Result: `HandleMove` (also the Windows `IFileMover` for restores) opens the source with `FILE_FLAG_OPEN_REPARSE_POINT`, refuses links and kind changes, requires the handle's final path to equal the expected path, and renames on that handle (`FileRenameInfoEx`, no overwrite); Windows tests cover a target and a parent swapped for junctions, an existing destination, and a junction swapped in between the gateway's checks and the move (refused, nothing moved, slot released); CI Windows green (run 39664338415).

### T-25 Recycle Bin deletion · `done` · W · deps: T-23
Result: the gateway asks `CannotRecycle` before acting and records where each item went; `WindowsRecycleBin` refuses network, non-fixed, turned-off (`NukeOnDelete`) or too-small Recycle Bins, recycles via `SHFileOperationW` with undo and a permanent-deletion warning, then verifies the item by its `$I` record, reporting a permanent deletion as `PermanentlyDeletedException`, never as recoverable; 4 Windows fixture tests (CI-only per D-017, clean up their own items) + 2 gateway tests; CI Windows green (run 39664818249).

### T-26 In-use checks · `done` · W · deps: T-23
Result: `InUseCheck` asks Restart Manager which processes hold a file, or any file in a folder, open and lists programs started from inside a folder; `WindowsPlatformOperations` combines handle moves, the Recycle Bin, in-use, elevation (DELETE-access probe, D-019) and same-volume (volume serial by handle) checks; a check that cannot be made makes the gateway refuse the item; 4 Windows tests hold fixture files open from the test process, incl. the gateway blocking one; CI Windows green (run 39665924421).

### T-27 Undo end to end · `done` · W · deps: T-24, T-25
Result: through the real Windows platform layer, Undo restores a quarantined file to its original path with its timestamps and attributes and a folder with its contents; a conflict at the original path leaves the item in quarantine; an interrupted quarantine run and an interrupted Undo are recovered at startup and the rest can then be undone; `TempRoot` now clears read-only files on cleanup; 5 Windows tests; CI Windows green (run 39665924421).

### Slice 7: UI shell (WPF)

### T-28 App shell · `done` · W · deps: T-02
Result: D-002 accepted (WPF with its built-in Fluent theme; `Microsoft.Extensions.DependencyInjection` 10.0.13 and `CommunityToolkit.Mvvm` 8.4.2 recorded); `AppServices` composes everything around the one `ExecutionGateway`; `MainViewModel` navigates between pages; `PauseViewModel` shows and changes the gateway's own `GlobalPause` (persisted); `StartupTasks` settles interrupted operations and purges expired quarantine at launch; 5 view-model/composition tests; CI Windows green (run 39665924421). The UI itself has not been looked at on a real desktop yet.

### T-29 First run · `done` · W · deps: T-28, T-06
Result: `FirstRunViewModel` states what is inspected, what is stored (the app's data folder) and the D-018 network sentence, then runs `Discovery` (read-only, user folder only): known caches first so a finding shows quickly, then large folders, duplicates of 1 MB or more, version groups and leftover app data; results go to `FindingsStore`, and first run is marked complete only when discovery ends (Stop changes nothing); 4 tests incl. real discovery on a fixture profile that is left unchanged; CI Windows green (run 39666291831).

### T-30 Overview and details · `done` · W · deps: T-29, T-19
Result: `OverviewViewModel` shows the three most useful recommendations (actions first, largest estimate first) and counts the rest; each `RecommendationItemViewModel` answers the six §7 questions and shows confidence, impact and recovery as separate lines, with rationale, evidence, exact operations and left-alone items behind Details; sizes are labeled measured or estimated; 5 tests; CI Windows green (run 39667002052).

### T-31 Preview and approve · `done` · W · deps: T-30, T-23
Result: `PreviewViewModel` lists the exact items (each can be left out), what is left alone and why, the expected effect for the current selection, and recovery/retention terms; Approve runs only the selected items through the gateway, Decline records the recommendation, Back changes nothing; `DeclinedRecommendations` (Policy, local JSON) keeps it hidden unless its evidence changes or its estimate at least doubles; keys now use evidence paths only; 5 portable + 4 app tests; CI Windows green (run 39667327100).

### T-32 Activity history and Undo · `done` · W · deps: T-31, T-27
Result: `ActivityViewModel` lists every ledger transaction newest first: what changed, why, the outcome per item, the quarantine deadline, and the restore limits for each operation type; Undo is offered only where `Restorer` supports it; Undo and the expiry purge now run through `ExecutionGateway` (refused while paused; startup skips the purge while paused); 5 app tests + 1 gateway test; CI Windows green (run 39667638896).

### T-33 Storage detail view · `done` · W · deps: T-30
Result: `StorageViewModel` (Storage page) lists the folders in the user folder biggest first, with drill-down/Up, files directly in each folder, a share bar and the largest file types; folder sizes are labeled "(measured)" (lower bounds when the scan was incomplete), what could be freed is labeled "estimated, not guaranteed" and comes from the suggested actions; "Measure again" reruns read-only discovery; `StorageSummary.Children`; no treemap; 1 portable + 5 app tests; CI Windows green (run 39748580784).

### T-34 Settings and local data · `done` · W · deps: T-28
Result: `SettingsViewModel` (Settings page) sets the quarantine period (1-90 days, saved in `settings.json`; applies to newly quarantined items, recommendations are rebuilt, and the gateway refuses a quarantine recommendation showing a different period), adds and removes exclusions, lists everything the app keeps with its size, and clears it after confirmation (`TransactionLedger.Clear` compacts the file); clearing is refused while quarantine holds items or a change is unfinished (D-020); 3 portable + 3 app tests; CI Windows green (run 39753530796).

### Slice 8: Delivery and Phase 1 exit

### T-35 Update flow · `done` · W · deps: T-28
Result: Velopack 1.2.161 (= `VPK_VERSION`; the CI Linux job checks they match) with a custom `Program.Main` (`SetAutoApplyOnStartup(false)`); `UpdatesViewModel` (Updates page) discloses what contacting GitHub involves and does nothing until "Check for updates", then downloads and installs only on "Install and restart"; D-010 UX decided (manual, two steps); 4 app tests; CI Windows green (run 39753530796).

### T-36 Privacy check · `doing (cloud, 2026-10-22)` · W · deps: T-35
Refs: §3.5, §9
Done when:
- The app's observed network behavior matches what it discloses: only the update check, only when the user starts it.
- The result is recorded in `docs/`.

### T-37 Performance baseline · `doing (cloud, 2026-10-22)` · W · deps: T-07
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
