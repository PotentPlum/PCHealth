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

### T-41 Version review · `done` · W · deps: T-40
Result: The Duplicates page has a "Possible versions, not copies" tab: each version group side by side (paths, sizes, modified with the newest marked, created), with an explanation of why similar names are not evidence; nothing is preselected. `RecommendationBuilder.ForVersions` turns only the ticked files into a Recycle Bin action through the usual preview and gateway; excluded or protected files are refused. A real discovery run shows same-size versions are never verified duplicates. D-024; 3 new tests.

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

### T-46 Catalog: apps that must be closed first · `done` · P · deps: —
Result: Cache rules can list `closeFirst` apps (bare process name and display name); the Chrome and Edge caches name their browsers. Catalog schema version 2; `CacheCatalog.Load` refuses other versions and invalid rules. Recommendations and both previews say "Close X first.", and the gateway refuses the request while a named app runs (`IPlatformOperations.RunningProcesses`), or when it can't check. D-025; 4 new tests.

### T-47 Catalog: more caches · `done` · W · deps: T-46
Result: 13 new rules (Chrome and Edge script caches, Firefox cache and startup cache, Discord cache and script cache, Steam web cache, VS Code compiled-code cache and extension downloads, pip, Yarn and Go build caches, Store apps' `TempState`), each with a why, a rebuild note and a fixture test; apps that own a cache are named to close first. Teams, Spotify, Office file cache and Steam shader cache stay out (D-026). A test on the CI Windows runner checks every rule's location forms a full, unprotected path. 15 new tests.

### Slice 5: File organization

### T-48 File triage suggestions · `done` · P · deps: —
Result: New `PCHealth.Organization` project; `FileTriage` suggests installers, extracted archives (zip listing compared by path and size), disk images, screenshots, possible financial/tax and signed documents (name keywords, PDF and Office properties, PDF signature dictionary) and likely older versions, each with a confidence and evidence. Financial and signed are sensitive and never preselected. New `TriageCategory` exclusion kind. D-027; 14 new tests on a fixture tree.

### T-49 Project detection · `done` · P · deps: T-48
Result: `ProjectDetector` finds roots from `.sln`/`.slnx`/`.csproj`, `package.json`, `pyproject.toml`, `Cargo.toml`, `.git`, and creative files (`.prproj`, `.aep`, `.blend`, `.als`, `.flp`, `.psd` with siblings), skipping manifests inside `node_modules`, `bin`, `obj` and similar. Each `DetectedProject` reports its files, measured size, last activity ("Not changed since …"), parent root, the sync service whose folder holds it, and its online-only files; it has no removal suggestion. 13 new tests, including nested roots and a OneDrive project.

### T-50 Dependency scanning · `done` · P · deps: T-49
Result: `DependencyScanner` reads `.sln`/`.slnx`, `*.csproj` references, hint paths and imports, `package.json` `file:`/`link:`, `.code-workspace` folders, batch and PowerShell scripts, and `.lnk` targets (parsed from bytes). Paths are classified with Windows rules on every OS (full, network, leaving the folder, variable). A folder with a dependency is move-restricted with its reasons listed; unread formats, invalid and oversized files are listed as not checked, and the summary never claims completeness. D-028; 36 new tests, including relative escapes, UNC, environment variables and mixed separators.

### T-51 Move operation · `done` · W · deps: —
Result: `MoveToFolder` operation (recovery `MoveBack`, ledger `MoveToFolder`, finding category `Organization`), built from the user's choice by `RecommendationBuilder.ForMove`. The gateway checks the destination by handle right before each item (real folder, not protected or excluded, same drive, no name clash, not inside the item), records source and destination before moving, and verifies after. Undo moves items back and skips any whose original path is taken; interrupted moves are settled at startup. Personal-folder protection is relaxed for items inside them only (D-029). 16 new tests; the Windows test refuses a real junction and undoes by handle.

### T-52 Organize page · `done` · W · deps: T-48, T-50, T-51
Result: New "Organize" page (`OrganizeViewModel`, `OrganizeAnalysis`). It scans Desktop, Downloads and Documents (known-folder API) on request and shows suggested categories with confidence, evidence and paths, plus projects with size, activity and dependency findings. Files inside a project stay with it. The user unticks items and picks a destination with the folder dialog; the preview lists every move with its destination. Move-restricted projects can't be ticked and say why; sensitive categories need a second confirmation. Moves run through the gateway, appear in Activity and can be undone. 6 view-model tests on a fixture tree.

### T-53 Archive suggestions for inactive projects · `done` · W · deps: T-52
Result: `ProjectArchive` marks projects whose newest file is older than a threshold (default 12 months, 1–120, saved in settings and changed on the Organize page). Inactive projects can be moved whole into an archive folder the user picks, through the T-51 move; restricted projects can't be ticked. The wording states facts and what archiving does; tests check it never says delete, remove, unused or similar. 8 portable tests and 1 view-model test.

### Slice 6: Optional compression

### T-54 Compression savings estimate · `done` · W · deps: —
Result: Mechanism decided: WOF XPRESS8K, not NTFS LZNT1 (D-030). `CompressionEstimator` (Inventory) compresses a bounded sample in memory: 32 MB in total, at most 1 MB per file, chunks spread through each file. It skips already compressed formats, files under 4 KB and links, with reasons, and reports an `EstimatedSize` with its sample size and basis (`CompressionEstimate` in Core). `WofCompressor` uses the Windows Compression API's raw XPRESS Huffman. Files are only read. 6 portable tests (incompressible, empty folder, sample bound, nothing changed) and 3 Windows tests.

### T-55 Compress and decompress through the gateway · `done` · W · deps: T-54
Result: `CompressInPlace` operation (recovery `Decompress`, category `Compression`, `RecommendationBuilder.ForCompression`). The gateway checks NTFS, encryption, existing compression, placeholders and use right before each file, skips with a reason (also when not beneficial), and records size on disk before and after, so the observed bytes are measured. Undo decompresses files still compressed and refuses up front when free space is too small; interruptions are settled at startup. `WofCompression` implements it with WOF XPRESS8K. D-031; 7 portable tests and 2 Windows tests on the CI runner.

### T-56 Compression in the app · `done` · W · deps: T-55
Result: The Storage page offers "Estimate compression" for each folder and for the folder being looked at. It shows the estimate with its sample and basis, the read and compatibility trade-offs, and that savings depend on the files. "Compress…" goes to the preview, which shows the estimated saving for the selected files. After approval the gateway compresses, and Activity shows "Compressed N items: X saved (measured)" with Undo. Compression is offered only when the user asks for an estimate, never from age. 2 view-model tests.

### Slice 7: Startup and background analysis (read-only)

### T-57 Startup inventory · `done` · W · deps: —
Result: New `PCHealth.Maintenance` (portable `StartupInventoryReader`, task XML parsing, StartupApproved decoding, command parsing) and `PCHealth.Maintenance.Windows` (`WindowsStartupSources`). It reads Run/RunOnce in both hives and views, the Startup folders (`.lnk` targets), boot or logon scheduled tasks and automatic services, each with mechanism, command, program, file existence, publisher (WinVerifyTrust, cache only) and enabled state, and lists every source it could not read. Nothing is changed. D-032; 11 portable tests on fakes and 3 Windows tests on the CI runner.

### T-58 Resource observation · `done` · W · deps: T-57
Result: `ResourceObserver` samples per-process CPU time and working set on request: a bounded window (2 minutes by default, at most 30), cancellable, on a lowest-priority thread. It groups by program path, lists protected processes as unmeasured, and maps startup items by image path (shared hosts are not attributed). Results carry their window and caveats, with no combined score. Boot timing comes from Diagnostics-Performance event 100 through `wevtapi`, or reads "not available without administrator rights". D-033; 5 portable tests with scripted samples, and 2 Windows tests, one of which observes a busy fixture PowerShell process.

### T-59 Maintenance page · `done` · W · deps: T-58
Result: New "Maintenance" page. It lists startup items with mechanism, state and its evidence, where each is registered, the command, the program and whether it exists, the publisher, and the latest observation, plus boot timing or why it is unavailable. Each item says how to change it yourself (Task Manager Startup apps, Task Scheduler, Services); the app changes nothing. The page states what it reads and that results stay in memory. The observation is started and stopped by the user, and its measurements are shown side by side with caveats. 3 view-model tests on fakes.

### Slice 8: Diagnostic timeline

### T-60 Timeline events · `done` · W · deps: —
Result: `TimelineReader` reads app crashes and hangs (1000/1001/1002), unexpected shutdowns (Kernel-Power 41, EventLog 6008), service and driver start failures (SCM 7000–7034), Windows Update installs and failures (19/20), MSI installs and removals, and Reliability Monitor records (WMI, minus duplicates) into one `TimelineEvent` model with evidence. Every source that could not be read is listed. D-034; 14 portable tests on recorded event XML and 5 Windows tests that read the runner's real logs read-only.

### T-61 Guided slowdown investigation · `done` · W · deps: T-60, T-57
Result: "When did it start?" on the Maintenance page. The user picks a day; the page shows the timeline from 7 days before to 1 day after and highlights updates, installs and removals, drivers that did not load, and new services (SCM 7045, added to the timeline) before that day as "Possibly related". Tests keep cause words and driver judgments out of every text. Nothing is changed or downloaded. D-035; 4 portable tests and 1 view-model test.

### Slice 9: Phase 2 wrap-up

### T-62 Privacy and permissions re-check · `todo` · W · deps: T-45, T-53, T-56, T-59, T-61
Refs: §3.5, §6.5, §9 "Privacy" · area: tests/PCHealth.App.Tests/PrivacyTests.cs, scripts/ci, docs/privacy.md
Done when:
- Every new flow runs under the network listener with no network events, and the CI launch check still passes.
- `docs/privacy.md` lists the new local data (the inventory snapshot) and what the helper and the maintenance features read.
- The Settings stored-data list and Clear cover the new files.

### T-63 Second release · `doing (cloud, 2026-10-28)` · U · deps: T-62
Refs: D-009 · CLAUDE.md "CI and releases"
Done when:
- The user asks for v0.2.0.
- The tag is pushed from a green `main`.
- The second PC updates to it through the app.

Released on the user's request on 2026-10-28, before T-62 (blocked on T-39): v0.2.0 was tagged by the Release workflow from `main` at 7ae97ad after CI passed there. The GitHub Release has the installer, MSI, portable zip and full and delta update packages.
Next: the user updates the second PC through Updates, checks the Organize, Storage compression and Maintenance pages there, and sets this to `done`.

---

## Later phases (not sliced yet)

Slice these into tasks only after Phase 2 is done. Until then they're out of scope.

- **Phase 3** (§8):
  - developer artifacts
  - WSL2/Docker virtual disks
  - creative caches and local AI model inventory
  - advanced diagnostics
- **Deferred** (§8): see the spec. Never start these without an explicit decision.
