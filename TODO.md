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

## Phase 1: Establish value and trust (spec §8) · `done`

T-01 to T-38a are done; v0.1.0 is released ([GitHub release](https://github.com/PotentPlum/PCHealth/releases/tag/v0.1.0)). Each task's result is in `git log --grep "T-NN"`, and the full Phase 1 task list is in this file at commit f0ea2ca.

Two follow-ups need the user's PCs. Nothing in Phase 2 waits for them.

### T-37b Performance baseline on the host · `todo` · U · deps: —
Refs: §6.1, §9 · docs/benchmarks.md
Done when:
- On the host, the app's **Benchmark** page (T-69, in the next release) has been run, or `dotnet run --project tools/PCHealth.Benchmarks -c Release -- --machine "host" --out result.md`.
- The result is added to `docs/benchmarks.md` under "Host" as a measurement, not a guarantee.

### T-38b Install and update on the second PC · `todo` · U · deps: —
Refs: D-009, D-010 · README "Installing on another PC"
Done when:
- v0.1.0 is installed on the second PC from `PCHealth-win-Setup.exe` and the app starts.
- The user asks for v0.1.1, it is tagged from a green `main`, and Updates → "Check for updates" → "Install and restart" on the second PC brings it to v0.1.1.

### Follow-ups from using v0.1.0

### T-64 App data apart from the install folder · `done` · W · deps: —
Result: Data lives in `%LocalAppData%\PCHealthData`; `DataMigration` moves the 0.1.0 files and quarantine slots at startup (renames, resumable, never overwrites, fails closed) and `TransactionLedger.MoveRecoveryLocations` repoints the ledger; both folders are `AppData`-protected; D-021; 4 migration/protection tests + 1 ledger test.

### T-65 Install into Program Files · `done` · W · deps: T-64
Result: Releases add `PCHealth-win.msi` (`vpk --msi --instLocation PerMachine`, "PC Health Monitor" by PotentPlum, app icon); Velopack's Update.exe elevates to apply updates there; CI packaging check builds and checks the MSI; D-022; README install and 0.1.0 move steps.

### T-66 Scan progress · `done` · W · deps: —
Result: Discovery reports its five steps and throttled counts; `DiscoveryProgressViewModel` shows steps, files/folders/bytes, elapsed and current folder, an estimated percentage and time left from the last scan's file count (kept in settings.json), and a measured percentage and time left for duplicate hashing (`HashProgress`, total known up front); first-run and Storage pages; 6 tests.

### T-67 Clean-up buttons · `done` · W · deps: —
Result: "Clean up…" per suggestion; "Clean up all N suggested" opens `BatchPreviewViewModel` (per-action checkbox, exact paths, left-alone items, recovery terms, total) and runs each selected action as its own gateway transaction after one approval; items needing a choice are counted and excluded; the overview can list all suggestions; 3 tests.

### T-68 App icon · `done` · W · deps: —
Result: A drive with a heartbeat line on a blue tile, 16–256 px `.ico` drawn by `tools/icon/make_icon.py`; used by the exe, window/taskbar and both installers.

### T-69 Benchmark from the app · `done` · W · deps: T-37a
Result: Benchmark code moved to the portable `PCHealth.Benchmarking` library (CLI is now a thin wrapper); the Benchmark page runs it with a free-space check and Stop, deletes its test files, saves each report as a new file in `Documents\PC Health Monitor\Benchmarks` and shows the path with Open folder and Copy path; D-023; 3 tests.

---

## Phase 2: Organization and controlled maintenance (spec §8)

Rules that carry over from Phase 1: discovery stays read-only; every file change goes through the gateway with a ledger transaction and a preview; startup and service changes are out of scope (analysis only); no unattended or bulk automatic moves (§5.2, §8).

Of the tasks that start work, only T-39 needs the user. It gates the MFT/USN slice (T-42 to T-45); every other slice can start now. Suggested order: T-40, T-46 and T-48 first. They have no deps and reuse Phase 1 pieces.

### Slice 1: Decisions

### T-39 Decide the elevated helper · `todo` · U · deps: —
Refs: D-007 (proposal written), D-019 · §6.1, §6.5
Done when:
- The user accepts, changes or rejects the D-007 proposal: a separate on-demand elevated helper, a user-only named pipe, and read-only volume access only.
- D-007 says `Accepted` or `Rejected`. If rejected, T-42 to T-45 are removed and the recursive scanner stays the only inventory.

### Slice 2: Duplicate and version review

### T-40 Duplicate review page · `done` · W · deps: —
Result: A Duplicates page lists verified groups, biggest first, with each copy's paths, measured size, modified and created times side by side, and the evidence on request (size, head+tail digest, full hash, same-file check). Nothing is preselected. `RecommendationBuilder.ForDuplicates` refuses removing every copy, or a protected or excluded copy. The gateway compares each removed copy byte for byte with its kept copy right before acting. Activity can undo Recycle Bin moves (`IRecycleBin`, `WindowsRecycleBinRestore`). D-024; 11 new tests.

### T-41 Version review · `todo` · W · deps: T-40
Refs: §5.1 · area: src/PCHealth.App, src/PCHealth.Duplicates
Done when:
- Version candidates appear on a separate tab of the Duplicates page, labelled "possible versions, not copies".
- No file in a version group is preselected for removal, and the page says why similar names are not evidence.
- The user can compare files and send individual files to the Recycle Bin through the gateway.
- Tests cover "nothing preselected" and that version groups never appear among verified duplicates.

### Slice 3: Fast inventory (needs T-39 accepted)

### T-42 MFT record parser · `todo` · P · deps: T-39
Refs: §5.1 "Storage inventory", §6.1 · area: src/PCHealth.Inventory (new `Ntfs/` folder)
Done when:
- A portable parser turns raw 1 KB/4 KB MFT FILE records into entries: update-sequence fixups, `$STANDARD_INFORMATION`, `$FILE_NAME` (preferring Win32 names over DOS ones), `$DATA` sizes (resident and non-resident), attribute lists, the in-use and directory flags, and hard links.
- Malformed records are reported, never thrown past the caller.
- Tests use synthetic records built byte by byte. No real volume is read.

### T-43 Volume inventory in the elevated helper · `todo` · W · deps: T-42
Refs: D-007 · §6.1 · area: new `src/PCHealth.Helper` (net10.0-windows), src/PCHealth.Inventory.Windows
Done when:
- The helper reads `$MFT` through a read-only volume handle, feeds the T-42 parser, and streams entries to the app over the D-007 pipe.
- An `MftScanner : IInventoryScanner` in the app returns the same paths, sizes and timestamps as `RecursiveScanner` for a fixture subtree on the CI runner.
- Tests that need administrator rights run only in CI, behind an opt-in variable like the Recycle Bin tests (D-017).
- The pipe refuses a client that is not the current user, and a wrong secret.

### T-44 Incremental updates from the USN journal · `todo` · W · deps: T-43
Refs: §5.1, §6.1 · area: src/PCHealth.Inventory.Windows
Done when:
- The last inventory and the journal ID and next USN are stored locally (a new data file listed in Settings → stored data).
- The next discovery applies create, delete, rename and size changes from the journal instead of a full read.
- A recreated journal, a wrapped USN or a changed volume triggers a full read and says so.
- Tests cover each of those cases against fixture changes.

### T-45 Choosing the inventory · `todo` · W · deps: T-44
Refs: §3.5, §6.5 · D-007, D-018 · area: src/PCHealth.App
Done when:
- The first run and Settings explain the faster scan and its administrator prompt. The recursive scan stays the default until the user opts in.
- Discovery uses the MFT on opted-in NTFS volumes and falls back to the recursive scanner otherwise (FAT/exFAT, network drives, a denied prompt, a helper error).
- The storage page says which method measured the numbers.
- Benchmarks gain an MFT stage, and `docs/benchmarks.md` gets runner numbers.

### Slice 4: More application caches

### T-46 Catalog: apps that must be closed first · `todo` · P · deps: —
Refs: §5.1 "Application residue and cache cleanup" · D-015 · area: src/PCHealth.Policy/Catalog
Done when:
- Catalog rules can name processes that must not be running, for example `firefox`.
- Recommendations for those rules say "Close X first", and the gateway refuses while a named process runs. The check goes through `IPlatformOperations`, so it can be faked in portable tests.
- The catalog schema version goes up, and older catalog files are refused.

### T-47 Catalog: more caches · `todo` · W · deps: T-46
Refs: §5.1 · area: src/PCHealth.Policy/Catalog/known-caches.json
Done when:
- At least 10 new rules exist, each with a why, a "what is rebuilt" note and a fixture test. Candidates:
  - Firefox cache2
  - new Teams cache
  - Discord cache and code cache
  - Spotify storage
  - Steam htmlcache and shader cache
  - VS Code CachedData and CachedExtensionVSIXs
  - pip cache
  - Yarn cache
  - Windows Store app TempState
  - Office file cache
- Only folders the app can document as regenerable are included. Anything that holds user data, sign-ins or offline content the user chose to keep stays out.
- Every rule was checked on the CI Windows runner for its expected location format.

### Slice 5: File organization

### T-48 File triage suggestions · `todo` · P · deps: —
Refs: §5.2 · area: new `src/PCHealth.Organization` (net10.0)
Done when:
- For Desktop, Downloads and Documents, rule-based suggestions group files into these suggested categories, each with a confidence and the evidence behind it:
  - installers
  - archives that already have an extracted folder next to them
  - disk images
  - screenshots
  - possible financial or tax documents, matched by name and properties
  - possible signed documents
  - likely superseded files
- Categories that may hold personal or sensitive documents are marked sensitive. They are never preselected and are always shown as suggestions.
- Exclusions apply at file, folder and category level. Category exclusions are a new exclusion kind.
- Tests run on a generated fixture tree.

### T-49 Project detection · `todo` · P · deps: T-48
Refs: §5.2 · area: src/PCHealth.Organization
Done when:
- Project roots are found from these manifests:
  - `.sln`, `.slnx`, `*.csproj`
  - `package.json`, `pyproject.toml`, `Cargo.toml`
  - a `.git` folder
  - creative project files: `.prproj`, `.aep`, `.blend`, `.als`, `.flp`, `.psd` with siblings
- Each root shows its files, total measured size and last activity (newest modified time).
- An inactive project is described as "not changed since …". It is never described as expendable, and nothing is recommended for removal on age alone.
- Tests cover nested roots and a project inside a cloud-sync folder.

### T-50 Dependency scanning · `todo` · P · deps: T-49
Refs: §5.2, §9 "Correctness" · area: src/PCHealth.Organization
Done when:
- These formats are parsed for absolute paths and paths that leave the folder:
  - `.sln`/`.slnx` project paths
  - `*.csproj` `ProjectReference`/`HintPath`/`Import`
  - `package.json` `file:` and `link:` dependencies
  - `.code-workspace` folders
  - `.bat`, `.cmd` and `.ps1` scripts
  - `.lnk` targets
- A folder with such a dependency is move-restricted, and the reasons are listed.
- Formats the scanner can't read are listed as "not checked", so a clean result never claims completeness.
- Tests include adversarial paths: relative escapes, UNC, environment variables and mixed separators.

### T-51 Move operation · `todo` · W · deps: —
Refs: §6.3, §6.4 · area: src/PCHealth.Core, src/PCHealth.Execution, src/PCHealth.Execution.Windows
Done when:
- A new `MoveToFolder` operation kind exists. The destination must be a folder the user chose, must not be protected or excluded, must not overwrite anything, and in this phase must be on the same volume. The rationale is recorded as a new decision.
- The gateway revalidates the source and destination by handle right before acting, and the ledger records both.
- Undo moves the items back, and refuses if something now occupies the original path.
- Tests cover a destination that is a reparse point, a name clash, a source changed after preview, a crash midway (settled at startup) and Undo.

### T-52 Organize page · `todo` · W · deps: T-48, T-50, T-51
Refs: §5.2, §7 · area: src/PCHealth.App
Done when:
- An "Organize" page shows suggested groups with their evidence and paths.
- The user can uncheck items and pick a destination before the preview. The preview lists every move.
- Move-restricted folders can't be selected, and the page says why. Sensitive categories need a second confirmation.
- Moves run through the gateway and appear in Activity with Undo.
- Tests drive the view model on a fixture tree.

### T-53 Archive suggestions for inactive projects · `todo` · W · deps: T-52
Refs: §5.2, §5.3 · area: src/PCHealth.Organization, src/PCHealth.App
Done when:
- Projects inactive past a threshold the user can change (default 12 months) can be moved to an archive folder the user chose. The move uses the T-51 operation and respects move restrictions.
- The wording never implies the project can be deleted.
- Tests cover the threshold and a restricted project.

### Slice 6: Optional compression

### T-54 Compression savings estimate · `todo` · W · deps: —
Refs: §5.3, §6.1 · rule 9 · area: src/PCHealth.Execution.Windows (or a new Windows project), src/PCHealth.Core
Done when:
- The mechanism is decided and recorded as a new decision: WOF (XPRESS/LZX) versus NTFS LZNT1, the volume and file-type restrictions, and what happens when a compressed file is written.
- For a selected folder, an estimate compresses a bounded sample in memory with the chosen algorithm. It skips already-compressed types and reports an `EstimatedSize` saving with its sample size. It is never presented as measured.
- Discovery stays read-only, and files are not modified by the estimate.
- Tests cover incompressible files, an empty folder and the sample bound.

### T-55 Compress and decompress through the gateway · `todo` · W · deps: T-54
Refs: §5.3, §6.3, §6.4 · area: src/PCHealth.Core, src/PCHealth.Execution, src/PCHealth.Execution.Windows
Done when:
- `CompressInPlace` is an operation kind with the usual revalidation. The ledger records the size before and the measured size on disk after.
- Undo decompresses, and refuses up front if the free space is too small.
- Files that are in use, encrypted, already compressed, or on unsupported volumes are skipped with a reason.
- Tests run on fixture files in CI.

### T-56 Compression in the app · `todo` · W · deps: T-55
Refs: §5.3, §7 · area: src/PCHealth.App
Done when:
- On the Storage page, a folder offers "Estimate compression". The preview shows the estimate, the performance and compatibility trade-offs, and that savings depend on the files.
- After approval, Activity shows the measured saving, and Undo is available.
- The app never recommends compression on age alone.
- Tests drive the view model.

### Slice 7: Startup and background analysis (read-only)

### T-57 Startup inventory · `todo` · W · deps: —
Refs: §5.4 "Startup and background resource analysis" · area: new `src/PCHealth.Maintenance` (net10.0) + `src/PCHealth.Maintenance.Windows`
Done when:
- Startup items are read, unelevated where Windows allows, with each item's mechanism, command, publisher (from the signature if any), whether the file exists, and the enabled state from `StartupApproved`. The sources:
  - Run and RunOnce keys in HKCU and HKLM, including WOW6432Node
  - the Startup folders
  - scheduled tasks with logon or boot triggers
  - auto-start services
- Anything that could not be read is reported as such.
- Nothing is changed. Tests read fixture registry hives or fakes and run on the CI runner.

### T-58 Resource observation · `todo` · W · deps: T-57
Refs: §5.4, §6.1, §6.5 · area: src/PCHealth.Maintenance.Windows
Done when:
- On the user's request, a bounded observation window (default 2 minutes, cancellable, low priority) samples per-process CPU time and working set and maps processes to startup items by image path.
- Boot timing comes from the Diagnostics-Performance log if it is readable; otherwise it is "not available without administrator rights".
- Measurements are shown separately with their window and caveats. There is no combined score.
- Tests use fake samples; one CI test observes a fixture process.

### T-59 Maintenance page · `todo` · W · deps: T-58
Refs: §5.4, §7 · area: src/PCHealth.App
Done when:
- A "Maintenance" page lists startup items with their mechanism, evidence and the latest observation.
- Each item explains how the user can change it themselves (Task Manager Startup, Services, Task Scheduler). The app changes nothing.
- The privacy and permissions text says what is read.
- Tests drive the view model.

### Slice 8: Diagnostic timeline

### T-60 Timeline events · `todo` · W · deps: —
Refs: §5.4 "Crash and driver investigation" · area: src/PCHealth.Maintenance(.Windows)
Done when:
- These events are read into one `TimelineEvent` model (time, source, kind, summary, evidence), unelevated where possible:
  - app crashes and hangs (Application 1000/1001/1002)
  - unexpected shutdowns (Kernel-Power 41, EventLog 6008)
  - service and driver start failures (Service Control Manager 7000–7034)
  - Windows Update installs
  - MSI installs and removals
  - reliability records
- Any source that could not be read is listed.
- Tests parse recorded event XML fixtures, and one CI test reads the runner's real logs read-only.

### T-61 Guided slowdown investigation · `todo` · W · deps: T-60, T-57
Refs: §5.4, §7 · area: src/PCHealth.App
Done when:
- A "When did it start?" flow shows the timeline around the chosen date and highlights updates, installs, driver events and startup changes that happened shortly before.
- It phrases them as possible explanations, never as root causes, and never treats an old or unsigned driver as defective.
- Nothing is changed or downloaded.
- Tests cover the wording rules and the time window.

### Slice 9: Phase 2 wrap-up

### T-62 Privacy and permissions re-check · `todo` · W · deps: T-45, T-53, T-56, T-59, T-61
Refs: §3.5, §6.5, §9 "Privacy" · area: tests/PCHealth.App.Tests/PrivacyTests.cs, scripts/ci, docs/privacy.md
Done when:
- Every new flow runs under the network listener with no network events, and the CI launch check still passes.
- `docs/privacy.md` lists the new local data (the inventory snapshot) and what the helper and the maintenance features read.
- The Settings stored-data list and Clear cover the new files.

### T-63 Second release · `todo` · U · deps: T-62
Refs: D-009 · CLAUDE.md "CI and releases"
Done when:
- The user asks for v0.2.0.
- The tag is pushed from a green `main`.
- The second PC updates to it through the app.

---

## Later phases (not sliced yet)

Slice these into tasks only after Phase 2 is done. Until then they're out of scope.

- **Phase 3** (§8):
  - developer artifacts
  - WSL2/Docker virtual disks
  - creative caches and local AI model inventory
  - advanced diagnostics
- **Deferred** (§8): see the spec. Never start these without an explicit decision.
