# Decisions log

Each entry: status (**Accepted**, **Provisional**: revisit before it matters, **Open**: undecided), decision, and why. Add entries in the slice that makes the decision; don't rewrite history. Supersede old entries with new ones instead.

## D-001 Language and runtime: all C# on .NET 10 LTS
**Provisional** (2026-10-21)

C# only. Native Windows APIs (MFT/USN, `GetFinalPathNameByHandle`, reparse points, Recycle Bin) are called via P/Invoke. C++ is added only if a benchmark shows C# cannot meet a defined target (spec Â§6.1).

.NET 10 because it is the current LTS. .NET 9 is STS and leaves support in November 2026.

## D-002 UI framework: WPF
**Accepted** (2026-10-21, user decision; built in T-28)

WPF is mature, builds with the plain .NET SDK, and needs no extra workloads. On .NET 10 it has a built-in Fluent theme, so a modern look doesn't need WinUI 3. Two UI dependencies are approved:
- `Microsoft.Extensions.DependencyInjection` for composition;
- `CommunityToolkit.Mvvm` for view models.

Pinned in `Directory.Packages.props` by T-28: `Microsoft.Extensions.DependencyInjection` 10.0.13 and `CommunityToolkit.Mvvm` 8.4.2. Only the toolkit's `ObservableObject` and `RelayCommand` are used, not its source generators. Neither package makes network calls.

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
**Accepted** (2026-10-21, T-20)

Suggested by the spec (§6.4). It is durable, local and inspectable.
- **Package:** `Microsoft.Data.Sqlite` 10.0.13. It brings the bundled native SQLite (`SQLitePCLRaw.bundle_e_sqlite3`) and makes no network calls.
- **Durability:** one file, opened with WAL journaling, `synchronous = FULL` and no connection pooling.
- **Schema version:** kept in `PRAGMA user_version`. A newer or unreadable file is refused, never overwritten.
- **History:** a saved transaction's purpose, items and recovery terms can't be rewritten; only its progress is updated.

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
- **Release run by hand** (added 2026-10-22, T-38, user decision): Cloud sessions can push only their working branch, not tags, so the Release workflow can also be started by hand on `main` with a version. It refuses unless all of these hold:
  - the version is a plain `X.Y.Z`;
  - the run is on `main`;
  - the tag doesn't exist yet;
  - CI passed on that exact commit.
  It creates the tag itself after the build is packaged, so a failed build leaves no tag behind. A tag pushed with `GITHUB_TOKEN` starts no second run.
- **Packaging check:** the CI Windows job runs the same publish and `vpk pack` steps on every push, without uploading. A packaging problem shows up before a tag, which can't be changed afterwards.

Why Velopack: it gives a one-time install on the other PC, then delta updates pulled from Releases with rollback, and needs no signing certificate (MSIX would). Releases come only from tags, so not every commit reaches the other PC.

Known limits:
- The repo is private, so Windows Actions minutes count double against the free quota.
- The exe is unsigned, so SmartScreen warns on first run.

## D-010 Update channel and update UX
**Accepted** (2026-10-21 channel; 2026-10-22 UX, T-35)

1. **Channel: public GitHub Releases.** The repo was made public on 2026-10-21, so Velopack can read releases without any token. The earlier options (a token on the other PC, or a shared-folder mirror) are no longer needed.
2. **UX: manual, in two steps.**
   - The app never checks, downloads or installs on its own: no check at startup, no schedule, no background download.
   - On the Updates page, "Check for updates" reads the GitHub release list and says whether a newer version exists.
   - Only after that does "Install and restart" download the new version and restart into it. `SetAutoApplyOnStartup(false)` makes sure a downloaded update is never applied at a later startup without that click.
   - The page discloses that checking and installing contact GitHub, that nothing about the user's files is sent, and that GitHub sees the IP address.
   - A copy not installed with the installer cannot check.
   - Silent auto-install stays ruled out for a tool that deletes files (§3.5, §6.2).
3. **Dependency:** `Velopack` NuGet 1.2.161, the same version as `VPK_VERSION` in `release.yml`. The CI Linux job fails if the two differ. The library contacts the network only through `UpdateManager.CheckForUpdatesAsync` and `DownloadUpdatesAsync`; the app calls them only from those two buttons. T-36 checks this against observed behavior.

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
**Proposed** (2026-10-22; the user decides in T-39)

The spec wants separate permission boundaries for read-only inventory, AI inference and elevated execution (§6.1, §6.5). Phase 1 ran unelevated in one process (D-019). Phase 2's fast inventory (MFT/USN, T-42 to T-45) needs a read-only handle to the volume, which Windows grants only to administrators. The proposal:

- **Two processes.** The app stays unelevated and keeps the UI, discovery, policy and the execution gateway. A separate `PCHealth.Helper.exe` ships in the same install folder.
- **On demand only.** The helper starts only after the user has opted in to the faster scan and starts a scan. It is launched through the normal administrator prompt (UAC). It exits when its work is done, when the app closes, or after 2 minutes idle. Declining the prompt falls back to the recursive scan.
- **Read-only in Phase 2.** The helper does exactly two things: read a volume's MFT, and read its USN journal from a given position. It changes nothing. Changes that need administrator rights stay refused, as in D-019.
- **IPC.** The app creates a named pipe with a random name and an ACL that admits only the current user. It passes the name and a one-time secret to the helper on its command line. The helper connects, proves the secret, and checks that the pipe's server is the app's own executable from the install folder. Messages are versioned and length-prefixed, and every request is checked against the two allowed operations.
- **Different-user elevation.** If a standard user elevates with someone else's administrator account, the helper refuses ("not supported"). The app falls back to the recursive scan.
- **AI inference** gets no process yet. There is no AI component, and the decision is made when one is planned.

## D-014 Default protected locations
**Accepted** (2026-10-21, T-15)

Protected by default (spec §3.4, §6.3). Each protection carries a user-readable reason.

- **Operating system:** the Windows, System and SystemX86 known folders.
- **Program files:** Program Files (x64, x86) and the per-user `%LocalAppData%\Programs`.
- **Personal folders:** Documents, Desktop, Pictures, Music and Videos. Downloads is not protected, because it is a common, user-visible cleanup area.
- **Boot and recovery:** `Boot`, `EFI`, `Recovery`, `System Volume Information`, `$WinREAgent`, `bootmgr`, `BOOTNXT`, `pagefile.sys`, `hiberfil.sys` and `swapfile.sys` at the root of the system drive.
- **Drive roots.**
- **Program binaries anywhere:** `.exe`, `.dll`, `.sys`, `.drv`, `.ocx`, `.cpl`, `.scr`, `.efi`, `.com` and `.mui`. They may belong to an installed application even outside Program Files. Cleaning up old installers will need an explicit, separately designed opt-in.

How protections apply:
- A protection covers the location and everything below it.
- A folder that *contains* a protected location is also blocked. For example, the user profile is blocked because it contains Documents.
- Folders come from the known-folder API (`SHGetKnownFolderPath`), so redirected and localized folders are found.
- Checks run on the final path from the path resolver (T-13), so links and 8.3 names can't bypass them.
- A folder that can't be resolved is still protected under its reported path.

## D-015 Application ownership evidence for residue detection
**Accepted** (2026-10-21, T-18)

Residue detection (spec §5.1) decides who owns a folder in AppData or ProgramData from the following sources, all read without changing anything:
- **Uninstall entries:** `HKLM\…\Uninstall` (64- and 32-bit views) and `HKCU\…\Uninstall`. Each entry gives a display name, publisher and install location.
- **AppX/MSIX packages:** the package families under the current user's `AppModel\Repository\Families` registry key. This avoids the WinRT `PackageManager` projection and its Windows SDK targeting pack. Package data folders in `%LocalAppData%\Packages` are matched to family names exactly.

Rules:
- **Owned:** a registration installs into the folder, or a name exactly matches or begins the registration's display name, publisher or install-folder name. Company forms and versions are ignored when comparing.
- **Uncertain:** the folder is reported for review only and never offered for bulk cleanup when any of these holds:
  - a registration source is missing or unreadable;
  - a name only resembles a registration's name;
  - part of the folder can't be read;
  - the folder holds program files;
  - something in it was written in the last 30 days.
- **Likely orphaned:** confidence is at most Medium, because folder names are only a clue to ownership.

Running processes, open handles, services and scheduled tasks are checked by the gateway right before acting (T-23, T-26), not during discovery.

## D-016 Quarantine location
**Accepted** (2026-10-21, user decision; T-21, T-24)

- Quarantine lives in one folder: `%LocalAppData%\PCHealthData\Quarantine` (`%LocalAppData%\PCHealth\Quarantine` until D-021).
- Items go in only by a same-volume rename by handle. An item on another volume is never copied into quarantine. Its finding offers the Recycle Bin instead, or nothing if the Recycle Bin is not suitable.
- Per-drive quarantine folders were considered and rejected, because they would leave a hidden folder on every drive.

## D-017 Recycle Bin tests run only in CI
**Accepted** (2026-10-21, user decision; T-25, T-27)

- Tests that send fixture files to the Recycle Bin put them in the real Recycle Bin of the machine running the tests.
- They therefore run only when `PCHEALTH_RECYCLE_BIN_TESTS=1` is set. The CI Windows job sets it; a host run skips them with that reason.
- The tests still use only fixture files created by `PCHealth.Testing`.

## D-018 First run: disclosure and scope
**Accepted** (2026-10-21, user decision; T-29)

- No automatic network calls before the update flow is decided (D-010, T-35).
- First run says: "Nothing leaves this PC. Checking for updates contacts GitHub only when you ask."
- First-run discovery is read-only and scans the user profile, not the whole drive, so the first finding arrives quickly.

## D-019 Phase 1 runs unelevated
**Accepted** (2026-10-21, user decision)

- The app never requests elevation in Phase 1, and D-007 stays open.
- A target that needs administrator rights to change, such as most of ProgramData, is still shown. The gateway refuses it with "needs administrator rights, not supported yet".

## D-020 Settings and clearing local data
**Accepted** (2026-10-22; T-34)

- The quarantine period can be set from 1 to 90 days, with 14 days as the default. A change applies only to items quarantined afterwards. Items already in quarantine keep the deadline they were given.
- Recommendations are rebuilt when the period or the exclusions change. If a quarantine recommendation shows a different period from the current one, the gateway refuses it, so the user never approves terms that would not apply.
- Clearing local data erases these: the ledger history (the file is compacted, not just emptied), exclusions, declined recommendations, settings and the first-run marker. The Pause state is kept.
- Clearing changes only the product's own files, so it does not go through the execution gateway. It is refused in two cases: while quarantine holds any item, or while a change is unfinished. Either way it would remove a recovery path the user may still be counting on. Items in the Recycle Bin stay restorable from the Recycle Bin itself, and the user is told so before clearing.

## D-021 The app's data lives apart from the install folder
**Accepted** (2026-10-27, T-64)

- **Why:** version 0.1.0 kept its data in `%LocalAppData%\PCHealth`, which is also where the per-user installer puts the app. Uninstalling deletes that folder: the history, and whatever is still in quarantine. That would break the recovery promise (§3.6).
- **Where now:** `%LocalAppData%\PCHealthData`, wherever the app is installed (D-022). It stays on the system volume, so quarantine moves are still same-volume renames (D-016).
- **Migration:** at every start, before anything opens the data, the app moves what is left in the old folder:
  - the ledger and its log files;
  - settings, exclusions, declines, the Pause state and the first-run marker;
  - each quarantine slot.

  Then the ledger's recovery locations are pointed at the new quarantine folder. Each step is a rename and is skipped once done, so an interrupted move finishes at the next start. Nothing already in the new folder is overwritten. If the move fails, the app shows why and stops, rather than starting with an empty history.
- **Protection:** both folders are protected locations (`AppData`), so no finding ever suggests changing the app's own data.

## D-022 Installed into Program Files
**Accepted** (2026-10-27, user decision; T-65)

- **Installer:** every release includes `PCHealth-win.msi` (`vpk pack --msi --instLocation PerMachine`). It is a standard Windows Installer package: it installs for all users into Program Files, appears in Apps & features, and asks for administrator rights once at install.
- **Name and icon:** the app is shown as "PC Health Monitor" by "PotentPlum", with the T-68 icon.
- **`Setup.exe`:** vpk still builds the one-click per-user `Setup.exe`, and existing per-user installs keep updating through it. The README points to the MSI.
- **Updates:** they still come from GitHub Releases on the two buttons (D-010). The install folder isn't writable without administrator rights, so Velopack's `Update.exe` asks for them (UAC) when an update is applied. The app itself still never asks for elevation (D-019).
- **Moving from 0.1.0:** install the MSI and start the new app once. That moves the data out of the old folder (D-021). Then uninstall the old per-user "PCHealth" entry.

## D-023 Benchmark from the app
**Accepted** (2026-10-27, user request; T-69)

- **What:** the Benchmark page runs the same code as `tools/PCHealth.Benchmarks`, which now lives in the portable `PCHealth.Benchmarking` library.
- **Writes:** the run writes its own test files into a new folder under the temp folder and deletes that folder afterwards, also when it is stopped or fails. It reads no user files. These are the app's own files, so like clearing local data (D-020) this doesn't go through the execution gateway. The gateway exists for changes to the user's files and system.
- **Space check:** the run is refused when less than twice the test files' size is free on the temp folder's drive.
- **Reports:** each one is saved as a new file in `Documents\PC Health Monitor\Benchmarks`, so it is easy to find and share. Existing files are never overwritten. The report names the machine only as "this PC" and contains no paths.
- **Not part of local data:** the reports belong to the user and are not covered by "Clear local data".

## D-024 Choosing duplicates, and Undo from the Recycle Bin
**Accepted** (2026-10-27; T-40)

- **The user's choice:** on the Duplicates page nothing is preselected. `RecommendationBuilder.ForDuplicates` refuses a choice that removes every copy, or that includes a protected or excluded copy. Only the chosen copies count: an excluded copy that stays is what the user asked for. An accepted choice becomes an ordinary recommendation, which is previewed, approved and run by the gateway.
- **Byte comparison before removal (as D-006 promised):** each duplicate removal names a kept copy (`ProposedOperation.KeptCopy`). Right before acting, the gateway checks that the kept copy is still there, is not a link, and has byte-for-byte the same contents as the copy being removed. Otherwise that copy is left alone. A request that would also remove its own kept copy is refused as a whole. The overview's duplicate suggestions use the same check.
- **Undo from the Recycle Bin:** Activity offers Undo for Recycle Bin moves while the items are still there. The restore works the way Explorer's Restore does. The `$R` item in the user's own `$Recycle.Bin` folder is moved back by handle, never overwriting, but only if its `$I` record names the original path. Then that `$I` record is removed. An item stays in the Recycle Bin when something now exists at its original path. Each Undo is a ledger transaction, as for quarantine.
- **Creation times:** the scanner now records each file's creation time, so copies can be compared on modified and created times side by side.
- **Possible versions (T-41):** they are on their own tab, "Possible versions, not copies". Their files differ in content, so a group never offers anything by itself and nothing in it is preselected. `RecommendationBuilder.ForVersions` turns only the files the user ticks into a Recycle Bin action, which is refused when versions or a ticked file are excluded or protected. There is no kept-copy check because the files are not copies.

## D-025 Apps that must be closed first
**Accepted** (2026-10-27; T-46)

- **Catalog:** a cache rule can list `closeFirst` apps, each with a bare process name (for example `chrome`, without `.exe` or a path) and the name people know it by. The shipped catalog names Chrome for the Chrome cache and Edge for the Edge cache.
- **Format version 2:** adding `closeFirst` raised the catalog schema to version 2. A catalog of any other version is refused rather than read, because an older file would silently lack the apps to close.
- **What the user sees:** the recommendation, the preview and the combined preview say "Close Google Chrome first."
- **Gateway:** before anything is recorded, the gateway asks the platform (`IPlatformOperations.RunningProcesses`) whether any named app is running, in any session. It refuses the whole request while one is. If that check can't be made, it also refuses (fail closed). It checks once per request. A file an app opens during the run is still caught per item by the existing in-use check, so a per-item process scan isn't needed.

## D-026 Which app caches the catalog includes
**Accepted** (2026-10-27; T-47)

- **Rule for inclusion:** a folder goes into the catalog only if the app itself rebuilds or downloads it again without asking the user. Anything that holds sign-ins, settings, messages, offline content the user chose to keep, or changes not yet synced stays out.
- **Added (13 rules):**
  - Chrome and Edge script caches;
  - Firefox cache and startup cache (the local profile only; bookmarks, history and sign-ins live in the roaming profile);
  - Discord cache and script cache;
  - Steam web cache;
  - Visual Studio Code compiled-code cache and downloaded extension packages;
  - pip, Yarn and Go build caches;
  - Store apps' `TempState` folders, for files unchanged for at least 7 days.
- **Close first:** browsers, Discord, Steam and VS Code are listed as apps to close first (D-025).
- **Left out:**
  - **New Teams cache:** clearing it resets the app and signs the user out.
  - **Spotify `Storage`:** it is also the default place for downloaded offline music.
  - **Office file cache:** it can hold documents waiting to upload to OneDrive or SharePoint.
  - **Steam shader cache:** it lives in Steam's install folder, which isn't a catalog location.
- **Checked on Windows:** every rule's location is checked on the CI Windows runner. Its token must resolve, each segment must be a valid name, and the full path must not be protected.

## D-027 File triage suggestions
**Accepted** (2026-10-28; T-48)

- **Where:** a new portable project, `PCHealth.Organization`. It works on read-only scans of Desktop, Downloads and Documents and never changes a file.
- **Rules, not a model:** suggestions come from names, extensions, sizes, times, sibling folders, zip listings and document properties. Each one carries a confidence and its evidence:
  - **Installers:** `.msi`/`.msix`/`.appx` packages (High); `.exe` files whose name says setup or install (Medium); other programs in Downloads (Low).
  - **Archives already extracted:** a folder with the archive's name is next to it. For a zip, its listing is compared with the folder by relative path and size (High when every file is there; Medium for some; Low for none or an unreadable zip). Other archive formats are not opened (Medium). Listings above 10,000 entries are not compared.
  - **Disk images:** by extension (High; `.img` Medium).
  - **Screenshots:** names that Windows and common tools give screenshots, in several languages (High); images in a folder named Screenshots (Medium).
  - **Possible financial or tax documents:** bank data formats such as `.ofx` (High); English, Dutch and German keywords in the name or in the document properties (Medium for one, High for both).
  - **Possible signed documents:** a PDF with a signature dictionary (High; validity not checked); keywords in the name (Medium).
  - **Likely older versions:** older files in a same-folder version group (T-41's stem rules), Medium.
- **Bounded property reads:** PDFs are read only in their first 64 KB and last 256 KB, and Office files only in `docProps/core.xml`. Properties in compressed object streams are missed, which is why matches stay "possible".
- **One category per file:** the sensitive categories are tried first, so a possible tax document is never filed under a category that may be preselected.
- **Sensitive:** financial and signed documents. They are never preselected. Elsewhere only High-confidence suggestions start ticked.
- **Exclusions:** files and folders use the existing kinds. Triage categories are a new exclusion kind, `TriageCategory`, so they can't be confused with finding categories. An excluded category withholds its files; it does not move them into another category.

## D-028 Dependency scanning before a move
**Accepted** (2026-10-28; T-50)

- **Formats read:**
  - `.sln` project lines and `.slnx` `Path` attributes;
  - `*.csproj` `ProjectReference`, `HintPath` and `Import`;
  - `package.json` `file:`/`link:` dependencies;
  - `.code-workspace` folders;
  - `.bat`/`.cmd`/`.ps1` path-like tokens, outside comments;
  - `.lnk` targets and working folders, read from the file's bytes per [MS-SHLLINK] without asking the shell to resolve them.
- **Windows rules everywhere:** paths are classified the same way on every OS. Both separators count, as do drive letters (also drive-relative `C:x`), UNC and `\\?\` prefixes, and `..` segments. A reference is a dependency when it is a full or rooted path, a network path, climbs out of the folder, or starts with a variable (`%VAR%`, `$env:`, `$(Prop)`, `~`). A shortcut's target is always a full path, so every shortcut is a dependency.
- **Variables the scanner understands:**
  - `%~dp0`, `$PSScriptRoot` and `$(MSBuildThisFileDirectory)`/`$(MSBuildProjectDirectory)`/`$(ProjectDir)` are the file's own folder;
  - `$(SolutionDir)` is assumed to be the scanned folder;
  - build-tool properties (`$(MSBuildExtensionsPath)`, `$(NuGetPackageRoot)`, …) are not the user's files and are skipped;
  - a variable in the middle of a path counts as one folder name;
  - batch variables the script sets itself, and PowerShell variables other than `$env:` and `$HOME`, are skipped because their literal values are checked where they are written.
- **Never a guarantee:** these are listed as "not checked": unreadable or invalid files, files over 4 MB (shortcuts over 1 MB), and formats that commonly hold paths but aren't parsed (other MSBuild projects, `.props`/`.targets`, shell and Python scripts, creative project files). Every summary says programs and settings elsewhere may still refer to the folder.
- **Not searched:** manifests inside `node_modules`, `bin`, `obj`, `.git` and similar belong to dependencies or build output, as in project detection (T-49).

## D-029 Moving items into a folder the user chose
**Accepted** (2026-10-28; T-51)

- **New operation kind:** `MoveToFolder`. The user chooses the items and the destination folder (`RecommendationBuilder.ForMove`). Its recovery method is `MoveBack`: Undo moves each item back unless something now exists at its original path, in which case that item stays where it went.
- **Same volume only in this phase.** On one volume a move is a rename by handle: it is atomic, needs no extra space, and an interruption leaves each item at exactly one of its two paths. A move between drives is a copy followed by a delete. That needs content verification, double the space for a while, and a way to settle half-copied items. That design is left for later, and the gateway refuses a destination on another drive.
- **Destination checks, right before each item:** done by handle. The folder must resolve to itself, with no link on its path. It must be a folder, must not be protected or excluded, must not be the item's current folder or inside the item, and nothing with the item's name may already exist there (nothing is overwritten). It must be on the same drive and need no administrator rights.
- **Ledger:** each item's original path and destination are recorded before the move (the destination as its recovery location), so an interruption is settled at startup from what is on disk. Found only at the destination means done; only at the original path means skipped; at both or neither means failed and left for the user. A move frees no space; expected and observed bytes are 0.
- **Protection for moves (amends D-014 for this kind only):** Documents, Desktop, Pictures, Music and Videos are protected so nothing in them is removed. A move the user approves item by item deletes nothing and can be undone. So items *inside* a personal folder may move, and a personal folder may receive moved items. The personal folders themselves stay protected, as do every other protected location, folders that contain one, and program files (`.exe`, `.dll`, …). Moving installers that are `.exe` files therefore waits for the separately designed opt-in D-014 asks for.
- **New finding category:** `Organization`. Users can exclude it like any other category.

## D-030 Compression mechanism: WOF XPRESS8K
**Accepted** (2026-10-28; T-54)

- **Chosen:** Windows Overlay Filter (WOF) compression with XPRESS Huffman in 8 KB chunks, the format `compact /EXE:XPRESS8K` uses. It is set and removed per file through the WOF API (T-55), and the path does not change.
- **Why not NTFS LZNT1 (the "Compressed" attribute):** LZNT1 compresses less, fragments files as they are rewritten, and is unavailable on volumes with clusters over 4 KB. XPRESS decompresses quickly enough that reading compressed files costs little. LZX compresses more but costs noticeably more CPU on every read, so it is not offered in this phase.
- **What happens when a compressed file is written:** WOF stores the compressed data as a read-only backing. The first write decompresses the whole file back to normal NTFS storage, so the saving disappears silently for files that change. That is why compression suits files that are rarely written, and why the app shows the measured result (T-55) instead of assuming it lasts.
- **Restrictions:** NTFS volumes only (not FAT, exFAT, ReFS or network drives). Not for encrypted (EFS) files, files with another reparse point (OneDrive placeholders, links, deduplicated files), files in use, or files under 4 KB (one cluster either way). Protected locations stay off limits as for any change.
- **Estimate:** a bounded sample (32 MB in total, at most 1 MB per file) is read and compressed in memory with the same XPRESS Huffman format through the Windows Compression API (`cabinet.dll`, raw mode, 8 KB chunks). Already compressed formats (archives, JPEG/PNG, audio, video, Office Open XML, …) are skipped with a reason. The result is an `EstimatedSize` with its sample size and basis, never a measurement. Files are only read.

## D-031 Compressing through the gateway
**Accepted** (2026-10-28; T-55)

- **Operation:** `CompressInPlace`, one file per operation, recovery method `Decompress`, finding category `Compression`. It is built from an estimate the user looked at (`RecommendationBuilder.ForCompression`), and refused when the estimate promises no saving.
- **Checks right before each file, besides the usual revalidation:** NTFS volume, not encrypted, not already compressed (WOF or the NTFS attribute), contents on this PC (not a cloud placeholder), and not in use. A file that fails a check is skipped with that reason. When WOF reports the compression would not save space, the file is skipped and left as it is.
- **Ledger:** each item records its size on disk before and after, both measured (`GetCompressedFileSize`). The transaction's observed bytes are the measured saving; its expected bytes are the estimate.
- **Undo:** decompresses the files that are still compressed, where they are. Files written since are already decompressed by Windows and are left out. Before anything changes, Undo checks each drive has room for the files' full size, and refuses with the numbers if not. Interrupted compressions and decompressions are settled at startup by asking WOF; the contents are unchanged either way.
- **Protection:** compression keeps the path and the contents and can be undone. Like a move (D-029), files *inside* personal folders may be compressed; program files and every other protected location may not.
- **Access:** the WOF call is tried with a read handle first. A write handle is used only when Windows asks for one.
