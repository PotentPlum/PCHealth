# PC Health Monitor — Product \& Technical Project Description (v3.0)

## 1\. Product vision

PC Health Monitor is a local-first Windows utility that helps technically curious users understand **what is consuming storage and system resources, recover space that can be reclaimed, and maintain their PC without losing control of it**.

Over time, Windows systems accumulate what this project calls *PC drift*: abandoned application caches, duplicate files, obsolete build outputs, oversized virtual disks, disorganized project assets, and unnecessary background activity. These issues are related, but they do not all have the same risk or require the same remedy. PC Health Monitor brings the investigation tools into one coherent application while keeping routine cleanup separate from specialist maintenance and system-level optimization.

The product should be technically capable under the hood and straightforward to operate at the surface. It is not intended to hide every detail from experienced users. Instead, it should show the most useful finding first and let users inspect the supporting evidence, affected paths, dependencies, expected benefit, and recovery options whenever they need more detail.

### Product promise

> \*\*Find what is wasting space or contributing to PC drift, understand the evidence, and choose what to change—with clear control and a recovery path wherever one can be supported.\*\*

The product must not promise that every recommendation is risk-free, that every operation can be completely reversed, or that a particular amount of disk space will always be recovered. It should make risk, uncertainty, and recovery limits visible before the user approves an action.

## 2\. Target users and product boundaries

The primary audience is the technically comfortable Windows user who wants more insight and control than a basic disk cleaner provides, without having to manually investigate every file, process, cache, and application setting. Developers, digital creators, and local-AI users are important secondary audiences because they can accumulate large amounts of specialized data and artifacts.

The application supports these needs through one consistent workflow, but it should not present every capability as equally important or enable every type of automation by default.

* **Core experience:** understand disk usage, find verified duplicates, identify evidence-backed cleanup candidates, and review system resource findings.
* **Organization experience:** review suggested file groups and project relationships, with explicit approval before files are moved or archived.
* **Specialist maintenance:** inspect developer artifacts, WSL2/Docker virtual disks, creative caches, and local AI model storage.
* **Troubleshooting experience:** investigate startup delays, background resource use, driver events, crashes, and changes that may correlate with a slowdown.

This is a broad power-user utility, not a collection of unrelated dashboards. Each capability must connect to a clear user goal and share a consistent pattern for evidence, approval, execution, and recovery.

## 3\. Product principles

### 3.1 Lead with findings, not implementation details

The main overview should answer: what stands out, what benefit may be available, and what the user can do next. Storage maps, event timelines, detailed dependency checks, model information, and low-level telemetry belong in contextual drill-downs. Advanced information should remain available to users who want it; it should not dominate the default screen.

### 3.2 Treat AI as an assistant, not an authority

Local machine-learning models can classify files, suggest project groupings, and prioritize likely cleanup opportunities. They must not independently authorize consequential file operations. AI findings are hypotheses supported by evidence, not proof that a file is disposable, unused, or safe to move.

Deterministic checks should validate relevant system state and dependencies. The user must be able to inspect and override proposed actions, exclude paths and categories, and decline recommendations without repeated pressure.

### 3.3 Separate confidence, impact, and recovery

Each actionable recommendation should communicate three independent dimensions:

* **Confidence:** how strong the evidence is that the finding is correctly identified and relevant.
* **Impact:** what will change, what benefit is expected, and what disruption or performance cost may result.
* **Recovery:** whether the action can be undone, what restoration covers, and any retention deadline or limitation.

These dimensions must not be collapsed into one reassuring “safe” score. High confidence does not imply low impact, and reversibility does not make an operation harmless.

### 3.4 Conservative by default, explicit by design

Initial discovery should be read-only wherever practical. Consequential changes require a clear preview and explicit approval. Personal documents, active project files, application binaries, system-critical locations, and uncertain ownership must be protected by default. Age or lack of recent activity alone is not sufficient evidence for deletion.

Routine scan scheduling may be automated according to user preferences. Destructive actions, file moves, service changes, virtual-disk compaction, and other high-impact operations should not run unattended by default.

### 3.5 Local-first, least privilege, resource-aware

File analysis and AI classification should run locally by default. The product must clearly disclose what information is inspected, where processing occurs, what the application stores, and whether any data leaves the PC. Any future feature that transfers data externally must be specifically disclosed and require explicit consent.

Use read-only access for discovery and request elevated privileges only for operations that need them. Background work should yield to active applications, respect battery and quiet-hour settings, and stop or defer deeper analysis when the user resumes demanding work.

### 3.6 Recovery is a user-facing feature

The action ledger is more than an internal log. Users need a readable history of what changed, why it was recommended, what outcome was observed, and which parts can be restored. The interface must state restoration limits honestly. A recorded transaction, symbolic link, or backup snapshot must never be presented as proof that all consequences can be reversed.

## 4\. Main user journeys

All major operations should follow a common sequence:

1. **Find:** identify a potential issue and collect supporting evidence.
2. **Explain:** show why it matters, which items or settings are involved, and what is uncertain.
3. **Preview:** list the proposed changes, estimated benefits, impact, exclusions, and recovery options.
4. **Approve:** let the user approve, decline, or adjust the selection.
5. **Execute and verify:** run the operation through the native execution layer and report what actually happened.
6. **Recover or inspect:** expose Undo where supported, preserve a readable activity record, and report partial failures clearly.

The preview should be specific enough for a technical user to make an informed decision. A summary may group many low-risk items, but the affected files, paths, checks, and exceptions must be inspectable before approval. Minimize unnecessary friction, not necessary understanding.

The product also needs a **global Pause** control that stops queued or running maintenance where feasible and prevents future automated modifications until re-enabled. Pausing must not delete the activity history or be confused with uninstalling the application.

## 5\. Functional scope

### 5.1 Core: storage analysis and evidence-backed cleanup

This is the product's starting point and the default experience.

#### Storage inventory and discovery

* Build and maintain a responsive file inventory with low-impact indexing and incremental change tracking where the file system supports it.
* On NTFS volumes, use the Master File Table (MFT) and USN Change Journal where practical to reduce repeated full scans. Provide a suitable fallback for volumes or conditions where these mechanisms are unavailable or unsuitable.
* Identify large directories, high-volume file categories, candidate caches, and older storage that merits review.
* Provide a clear storage summary by default. Offer treemap, sunburst, and last-access/age visualizations as optional investigation views, not as competing primary dashboards.
* Distinguish measured figures from estimates. “Potentially reclaimable” space must not be presented as guaranteed reclaimed space.

#### Verified duplicate detection and version review

* Use a staged comparison pipeline: file size and metadata filtering, a fast partial digest for suitable candidates, and a full cryptographic hash for final verification.
* Treat files as verified duplicates only when the content verification supports that conclusion.
* Group likely versions separately using names, metadata, and other available signals. Similar names or adjacent version numbers are not sufficient evidence that one version can be deleted.
* Let users compare file paths, sizes, timestamps, and other relevant details before selecting items for removal.

BLAKE3 is a candidate full-file hash; the implementation should choose and validate the exact algorithms and collision-handling policy as part of engineering design. Internal hashing stages should not clutter the normal UI, but the evidence behind a duplicate result should be inspectable.

#### Application residue and cache cleanup

* Identify likely orphaned application data in locations such as AppData and ProgramData by cross-referencing application/package registrations and available dependency evidence.
* Consider installer/package managers, uninstall registrations, AppX/MSIX information, running processes, open handles, services, scheduled tasks, and other relevant references before declaring an item orphaned.
* Prioritize well-understood, regenerable caches, crash dumps, and temporary data with a clear explanation of what may be rebuilt or downloaded afterward.
* When evidence is incomplete or contradictory, classify the item as uncertain and require additional review rather than escalating automatically to deletion.
* Route deletion through the Recycle Bin or a protected quarantine/staging area where technically appropriate. Make the retention period visible and configurable, and explain when permanent deletion will occur.

#### First-run value

The first session should begin with read-only discovery, explain the areas being inspected and any permission needs, and deliver one useful finding promptly. It should then offer a small, clearly explained action with a preview and a straightforward cancellation path. Users should not have to configure AI modes or learn storage visualizations before seeing value.

### 5.2 File organization and project awareness

This capability extends cleanup into helping users understand and organize unstructured data, particularly in Desktop, Downloads, and Documents.

* Use local classification, file metadata, directory proximity, temporal patterns, document properties, and supported project manifests to suggest logical groups.
* Present examples such as related project assets, possible tax/financial records, signed documents, installers, or likely superseded files as **suggested categories**, not certain truths.
* Show the evidence and individual paths behind a group. Require approval before moving, archiving, renaming, or deleting user files.
* Allow exclusions at file, folder, and category levels, and protect personal or potentially sensitive documents from automatic movement by default.
* Parse supported project formats and imports—including relevant solution/project files, creative-project formats, JSON manifests, and local scripts—to detect possible path dependencies before recommending a move.
* Mark a folder as move-restricted when known dependencies are found. If the scanner cannot fully understand a format or dependency, disclose that limitation; a clean scan is not a guarantee that every workflow will continue to work.
* Support archiving recommendations when project activity appears to have ceased, but never infer that an old project is expendable merely because it has not been opened recently.

Suggested organization is part of the product, but bulk automatic routing is not the default. Users must be able to inspect a proposed batch and adjust it before any paths change.

### 5.3 Optional compression and cold storage

For supported files and volumes, the application may offer in-place compression using Windows-supported compression capabilities, including NTFS compression or other applicable native mechanisms.

* Recommend compression only after estimating or measuring the likely space savings for the selected content.
* Explain potential read/write performance trade-offs and any compatibility or filesystem restrictions.
* Do not promise a universal savings percentage. Results depend on file type, existing compression, and workload.
* Keep compression as an explicit opt-in action with a preview and recorded outcome.

The unchanged-path benefit can be valuable, but the product must not treat compression as universally suitable or assume that an inactivity threshold alone is enough to recommend it.

### 5.4 Maintenance and performance insights

A maintenance area helps users understand background activity and investigate performance symptoms without turning routine cleanup into opaque system optimization.

#### Startup and background resource analysis

* Inspect startup entries, scheduled tasks, Windows services, and relevant process/resource observations to identify items that may affect boot time, idle CPU, or memory use.
* Present the underlying measurements and evidence: for example, observed startup timing, resource use, last observed activity, and the specific mechanism involved.
* Avoid inventing a composite “Software ROI” score that implies more certainty than the measurements support. Show disk usage, observed background resource consumption, and last-launch information separately, with context and caveats.
* Prefer recommendations and explanations over silently disabling components. Any change to startup behavior or services requires explicit review of the exact settings being changed and a dedicated restoration path where supported.

#### Crash and driver investigation

* Use Windows event logs, relevant WMI/driver information, Windows Update history, and available crash records to identify suspicious events or possible contributors to instability.
* Correlate updates, installations, startup changes, driver events, boot timing, and application crashes in a timeline when users investigate a slowdown.
* Describe correlations as possible explanations, not definitive root causes unless the evidence supports that conclusion.
* Do not treat an old or unsigned driver as automatically defective, and do not rely on untrusted third-party driver-download databases. The initial product should diagnose and explain; driver installation or replacement is outside the default cleanup workflow.

Registry cleanup is not part of the initial product scope. If a future diagnostic workflow identifies a narrowly defined, evidence-backed registry repair, it should be handled as a specialist operation with explicit consent and a tested backup/restore path—not as generic registry hygiene.

### 5.5 Specialist maintenance: developer, creator, and local-AI storage

Specialist capabilities preserve the original product's ambition, but live in a deliberate advanced area and may be introduced after the core cleanup and recovery experience is validated.

#### Developer artifact management

Identify potentially stale build and dependency artifacts such as `node\_modules`, Python `.venv`, Rust `target/`, `.gradle`, and NuGet, pip, or Cargo caches. Activity age is a discovery signal, not a deletion rule.

For each candidate, show the project path, approximate size, last observed activity, known manifest/dependency clues, whether it is likely to be reproducible, and the likely cost of rebuilding or redownloading it. Exclude active repositories and uncertain cases from bulk cleanup by default.

#### WSL2 and Docker virtual disk management

Detect potentially oversized WSL2 or Docker virtual disks, including `ext4.vhdx`, when the environment and permissions allow reliable inspection. Explain why deleting data inside a virtual disk may not reduce the host file size and outline the prerequisites for compaction.

Virtual-disk compaction should be a specialist, explicitly initiated workflow with checks for active workloads, adequate free space, supported configurations, and recovery considerations. Show a measured or qualified estimate and the actual result; never promise recovery of a fixed number of gigabytes.

#### Creative caches, GPU caches, and local AI models

Identify known media/application caches, eligible shader caches, and local AI model inventories where reliable detection is available. Explain likely regeneration/download costs and distinguish cache data from user-created assets or model weights.

A model or cache that appears unused may still be needed offline or for an infrequent workflow. Require inventory review before removing local model weights or large creator caches. Group results by application or ecosystem so users can make informed trade-offs.

### 5.6 Advanced application dormancy: deferred and separately gated

An application “Dormant Mode” that modifies services, scheduled tasks, startup entries, shell extensions, and launch shortcuts could reduce background activity for selected applications, but it introduces meaningful compatibility and support risks.

It is **not part of the core release**. Reconsider it only after the product has a proven change/restore framework, supported-application rules, compatibility testing, explicit opt-in, and a way to restore the exact previous configuration. It must never be enabled by a generic “optimize my PC” action.

Similarly, NTFS symbolic links or directory junctions may help with specific supported legacy-path scenarios, but they are not a general substitute for undo or guaranteed application compatibility. Do not create them by default. Introduce path redirection only for explicitly supported use cases with transparent location reporting, lifecycle management, and failure recovery.

## 6\. Technical architecture

### 6.1 Native inspection and execution layer

A deterministic native engine implemented in C++ and/or C# handles operating-system inspection, file metadata, dependency checks, validation, and privileged operations. Keep the design modular so read-only inventory, recommendation evaluation, and action execution can have separate permission boundaries.

* Prefer MFT/USN-based NTFS inventory and incremental updates where reliable, with fallback scans when needed.
* Use targeted Windows Event Tracing for Windows (ETW) providers only when they support a defined user-facing insight, such as process lifecycle, boot timing, or relevant hardware/driver events.
* Avoid collecting a continuous stream of low-level telemetry without a clear diagnostic purpose and user-visible value.
* Keep elevated execution narrow. The UI and AI inference components should not require administrator privileges merely to display findings.

Performance requirements—including indexing latency, background CPU/RAM use, and impact on active workloads—must be expressed as measurable engineering targets and benchmarked on representative hardware. Figures such as sub-second inventory, under-1% CPU overhead, or a fixed AI memory footprint must not be presented as achieved guarantees until validated across supported configurations.

### 6.2 Local AI proposal layer

Use local inference where it materially improves file triage or semantic grouping. The architecture may include:

* **Lightweight triage:** a quantized ONNX model for responsive classification and embedding workloads, scheduled or throttled to avoid competing with foreground tasks.
* **Deep analysis:** a larger local language/embedding model for explicit user-initiated audits or user-configured maintenance windows, only where the additional value justifies the resource use.

The lightweight path may use an available NPU or low-priority CPU execution; it must not require hardware that is not present. Deep-model weights should be released from memory when no longer needed, subject to platform constraints.

The AI layer runs with no file-write or command-execution authority. It returns typed, schema-validated proposals containing candidate paths, suggested operations, reasons, evidence references, uncertainty, and relevant metadata. The native layer independently validates every proposal. AI classification never bypasses policy checks or user approval.

“Local-first” and “air-gapped” must be implemented and described precisely. The product must make no network-transfer claims that are not verified by the actual build, dependencies, telemetry configuration, and update mechanism.

### 6.3 Execution validation and protection boundaries

All mutating operations pass through one native execution gateway. It must:

* Normalize and resolve the actual target path before acting, verify that it remains within an allowed scope, and protect against path traversal, junction/reparse-point tricks, and time-of-check/time-of-use (TOCTOU) races.
* On Windows, use handle-based path resolution and reparse-point-aware validation (for example, `GetFinalPathNameByHandle` and appropriate open flags) where supported and correct for the operation.
* Enforce explicit protected locations and file classes for operating-system components, installed application binaries, boot-critical areas, and other excluded targets.
* Recheck the relevant state immediately before modification, including application/package ownership, open handles, active processes, services, scheduled tasks, and known dependencies where those checks apply.
* Fail closed when the target path, ownership, or expected state cannot be validated.

The protection model should be strict, but the interface should present understandable protected/excluded locations and explain why a recommendation was blocked. A technical safeguard is not a substitute for an action preview or an honest account of remaining uncertainty.

### 6.4 Action ledger, quarantine, and restoration

Record each mutating operation as a transaction in a durable local ledger (SQLite is a candidate implementation). At minimum, capture:

* Transaction identifier, timestamp, operation type, and outcome.
* Why the action was proposed and which checks were performed.
* Exact affected paths/items and relevant before/after metadata.
* Expected and observed storage/resource effects when measurable.
* Recovery method, limitations, retention deadline, and any failed or partial steps.

Deletion should use the Recycle Bin or protected staging/quarantine where suitable. The initial default can use a visible retention window (for example, 14 days), but the duration must be configurable where feasible and communicated before the user approves the action. Permanent deletion must not occur silently while a user reasonably believes the item remains recoverable.

Implement Undo by operation type rather than assuming every action is transactionally reversible. File moves may be restorable if their original paths remain available; compression, cache removal, service changes, and partial operations have different recovery properties. State those differences explicitly. Registry snapshots are only relevant if a supported future operation changes registry state.

### 6.5 Permissions, privacy, and resource controls

* Start with read-only discovery when feasible.
* Request elevation only for a specific operation that requires it and explain the reason at the point of need.
* Keep AI inference isolated from privileged execution.
* Provide controls for scan scheduling, maintenance windows, quiet hours, battery behavior, and notifications.
* Allow users to disable specific categories of analysis or automation, pause background work, and inspect or clear locally retained product data.
* Avoid repeated notifications for findings users have dismissed unless materially new evidence changes the recommendation.

## 7\. User-facing information model

The default home screen should contain one prioritized system summary with a small number of meaningful findings, not a wall of metrics. Each finding should answer:

1. **What did we find?** A plain-English description and affected area.
2. **Why should I care?** Likely storage, performance, organization, or troubleshooting value.
3. **What is the evidence?** Relevant paths, measurements, registrations, dependency clues, or events.
4. **What will change?** Exact operations and their scope.
5. **What could happen afterward?** Rebuilds, downloads, compatibility effects, or performance trade-offs where relevant.
6. **Can I recover?** Undo method, retention deadline, and any limitations.

Provide an advanced/details view for users who want hashes, manifest references, service or scheduled-task names, event IDs, model rationale, or low-level measurements. Keep the terminology technically precise; explain specialized terms on demand rather than removing meaningful evidence.

Notifications should be limited to a prioritized summary, user-configured maintenance results, and genuinely important findings. Users should choose a cadence such as weekly summaries, recommendations only when opening the application, or important-problem notifications only.

## 8\. Scope and delivery sequence

### Phase 1 — Establish value and trust

* Read-only storage inventory and large-folder discovery.
* Verified duplicate review, with version candidates separated from identical files.
* Evidence-backed cleanup for well-understood application residue and caches.
* Clear preview, exclusions, explicit approval, quarantine/Recycle Bin handling where suitable, readable action history, and Undo where technically supported.
* Privacy and permission explanations, plus a global Pause control.
* A focused overview with optional detailed storage exploration.

Do not include automatic file organization, broad registry cleaning, Dormant Mode, or driver remediation in the initial release. The release should be considered successful only when classifications and recovery behavior are demonstrably reliable.

### Phase 2 — Organization and controlled maintenance

* Suggested file triage and project grouping.
* Dependency-aware file-move previews.
* Separate duplicate/version review, optional in-place compression, and more application-specific cache cleanup.
* Read-only startup/background resource analysis and guided performance investigation.
* More detailed diagnostic timelines where they support a concrete troubleshooting workflow.

Continue to require approval for file moves and system-setting changes. Do not introduce unattended destructive actions as a shortcut to convenience.

### Phase 3 — Specialist workflows

* Developer artifact review.
* WSL2/Docker virtual-disk analysis and compaction.
* Creative cache and local AI model inventory.
* Advanced storage and performance diagnostics.

Each module needs its own prerequisite checks, exclusions, recovery guidance, and tests. Advanced functionality should be discoverable by users who need it without complicating the core flow for everyone else.

### Deferred / separately gated

* Application Dormant Mode or dynamic freezing of services and shell extensions.
* General registry hygiene.
* Automatic relocation of arbitrary project folders, broad unattended cleanup, or automatic deletion based only on age or AI classification.
* Path-continuity redirection using symlinks/junctions except for specific, tested workflows.
* Automated driver installation or replacement.

These are not all inherently without value; they carry disproportionate compatibility or trust costs relative to the core promise and should only be reconsidered with clear evidence of demand and a validated safety/recovery design.

## 9\. Non-functional requirements and validation

* **Responsiveness:** foreground interaction takes priority over indexing and inference; measure startup, first useful finding, scan duration, CPU/RAM usage, and impact on active workloads.
* **Correctness:** verify duplicate identity, path canonicalization, exclusions, dependency detection, and action-state checks with automated tests and adversarial path cases.
* **Safety:** consequential actions require clear previews; uncertain findings fall back to review; blocked operations fail safely.
* **Recoverability:** test restoration from successful, interrupted, partially completed, and failed operations, including retention expiry behavior.
* **Privacy:** validate actual network behavior, local data storage, diagnostic exports, and permissions rather than relying on architectural intent alone.
* **Usability:** test whether users understand recommendations, identify exactly what they approve, locate changes afterward, use supported recovery, and pause automation without assistance.
* **Maintainability:** separate inventory, heuristics, policy validation, execution, and UI so that new specialist modules reuse the same safety and recovery framework.

Primary product measures should include time to first useful finding, useful space actually recovered, proportion of recommendations accepted or dismissed, user comprehension of proposed actions, successful restoration, post-action issues, recovery failures, and return usage driven by demonstrated value. Track mistakes and recovery failures separately; a fast operation that breaks a working project is not a success.

## 10\. Architecture summary

|Component|Responsibility|Key boundary|
|-|-|-|
|Native inventory service|File metadata, supported NTFS indexing, incremental change tracking, selected Windows telemetry|Read-only by default; resource use measured and throttled|
|Local triage engine|Classification, embeddings, candidate grouping|No write/execute authority; produces proposals only|
|Deep local analysis|More involved semantic grouping and specialist reasoning|Explicit or scheduled use; resource-aware and optional|
|Recommendation/policy layer|Evidence aggregation, confidence/impact/recovery classification, exclusions|AI suggestions do not override deterministic policy|
|Native execution gateway|Validated file/cache operations and approved system changes|Least privilege, canonical path validation, scope enforcement, revalidation immediately before action|
|Recovery and activity service|Transaction ledger, quarantine references, action history, supported Undo|Honest, operation-specific recovery guarantees|
|UI and scheduling layer|Prioritized findings, previews, detail views, notifications, Pause|User approval for consequential changes; user-controlled automation|

## 11\. Summary

PC Health Monitor should retain the original concept's technical depth: low-impact inventory, local AI-assisted triage, dependency-aware organization, verified duplicate detection, careful application-residue cleanup, background-resource analysis, and specialist support for developer, creator, virtual-disk, and local-AI storage.

The important change is how those capabilities are prioritized and governed. Storage recovery is the entry point; organization, optimization, and forensics become coherent optional workflows. AI proposes rather than decides. The native engine validates and executes. Users can inspect the evidence, control the scope, see what changed, and understand what recovery is actually possible.

The differentiator is not the number of subsystems or the sophistication of the implementation. It is giving technically capable Windows users a dependable way to investigate PC drift and take informed action without making their machine less predictable.

