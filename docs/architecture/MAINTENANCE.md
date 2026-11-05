# DeckCapture maintenance guide

This guide is for maintainers and future coding agents. Read the repository's `AGENTS.md`, [design plan](../DESIGN_AND_IMPLEMENTATION_PLAN.md), [API/component contracts](CONTRACTS.md), and the nearest component README before changing behavior. The [operations runbook](../operations/RUNBOOK.md) is authoritative for operating the current pilot.

## Implemented scope and readiness

The solution targets .NET 10. Its current projects are Domain, Supervisor, Capture, CaptureWorker, Delivery, and tests. The Supervisor uses SQLite for operational state, serves the vanilla browser client, and hosts the coordinator as a background service with Windows Service integration. `scripts/Start-DeckCapture.ps1`, `scripts/Publish-DeckCapture.ps1`, and `scripts/Test-DeckCapture.ps1` provide local start, publish, and test entry points. Publishing is framework-dependent by default; `-SelfContained -Runtime win-x64` includes the runtimes for both supervisor and worker. Production service registration, certificate provisioning and host/network provisioning remain separate deployment work. An optional paired media-tool bundle is resolved for default FFmpeg/ffprobe command names; explicit configured paths remain overrides.

The default persisted development configuration has four synthetic channels and a local test destination. The capture adapter uses an FFmpeg segment spool and FFprobe validation. It does not guarantee frame-accurate trigger placement. The default workstation FFmpeg build does not expose DeckLink. DeckLink acceptance requires the actual card, driver, deck, FFmpeg build, formats, source timing, audio, storage, and concurrent workload to be tested. A successful compile or synthetic integration test is not hardware acceptance.

Post-capture QC always includes FFprobe stream/container/frame-count validation and supervisor warnings. The `CaptureProfile.OfflineQcEnabled` switch optionally runs a full-file FFmpeg analyzer before package publication and adds native DeckCapture JSON measurements, intervals, filter availability, source hash, and warnings to `qc-summary.json`. The analyzer is serialized, resource-limited, reads the first video/audio streams, and treats measurements as indicators rather than pass/fail certification. This is not a QCTools XML report or calibrated broadcast scope. Do not describe the pilot as having full QCTools analysis, full preservation conformance, or archive certification. Permanent archive cataloging, collection search, and retrieval are outside the application.

The HTTP `/api/health` response is deliberately minimal and does not indicate capture readiness. Normal remote URLs require HTTPS and `DECKCAPTURE_ACCESS_TOKEN` with at least 24 characters; loopback HTTP is the default. Explicit `DECKCAPTURE_DEVELOPMENT_HTTP=1` allows trusted development LAN HTTP. A configured token, same-origin command checks and idempotency still apply in that mode. Tokens are not URL parameters. SMTP secrets are kept outside exported configuration and protected using ASP.NET Data Protection (DPAPI on Windows).

## Ownership and boundaries

- **Domain** owns shared JSON models, enums, and interfaces. Coordinate contract changes before changing them. JSON is camel case with string enum values.
- **Supervisor** owns authoritative job and delivery state, SQLite persistence, restart reconciliation, reservations, schedule admission, concurrency, retries, reporting, and API authorization/idempotency.
- **Capture/CaptureWorker** own one input session per worker, spooling, preview and sampled metrics, finalization, and FFprobe validation. Preview and monitoring must use the existing input session; never open the same device a second time for UI preview.
- **Delivery** owns copy, validated-prefix resume, hash verification, and no-overwrite publication. One shared engine instance enforces aggregate transfer rate.
- **Browser client** reads server state and submits revisioned commands. It must not simulate signal, progress, timers, QC, or successful outcomes.
- **Notifications** are an independent persistent outbox. SMTP failures must not alter or roll back a capture or verified delivery.

Configuration admits 1–16 channels and a configurable maximum of 1–16 concurrent deliveries, but those validation bounds are not tested hardware capacity. Only one prepared/waiting/active/finalizing recording is allowed per channel. A job's settings and metadata are immutable snapshots except the global experimental end-detection kill switch. The switch defaults off and disabling it cancels pending heuristic stop state on active jobs.

## Build, test, and publish

The browser uses native ES modules and has no build-time package dependencies. Node.js 18 or later is required only for its direct-import regression tests, which the standard test script runs alongside .NET checks. Consult `src/Supervisor/wwwroot/README.md` before changing a feature boundary or player lifecycle. Keep literal relative module imports acyclic: publishing fingerprints dependencies before importers, rejects missing modules and paths outside the web root, and stamps the entry point and stylesheet in the packaged HTML. Never edit hashed imports in source; stamping applies only to publish output.

Use the .NET SDK selected by `global.json`; restore only through the repository's configured NuGet source. Standard local commands are:

```powershell
dotnet build DeckCapture.sln -m:1 -nr:false
.\scripts\Test-DeckCapture.ps1
.\scripts\Publish-DeckCapture.ps1
```

`Test-DeckCapture.ps1` leaves capture media integration tests opt-in. Use `-IncludeMedia` only when FFmpeg and FFprobe are installed and `CaptureWorker` has been built. These tests create and retain files under `.local\capture-tests`; do not clean that evidence automatically. Synthetic test success does not establish source-frame accuracy, hardware lock/drop detection, DeckLink support, archive-server semantics, A/V sync, or four-channel throughput.

The publish script produces `artifacts\DeckCapture` by default and checks the supervisor, static UI, and capture worker files. The worker's complete runtime output (DLL, runtime/deps files, and dependent assemblies) must be under `capture-worker` beside the supervisor. Do not package only the worker DLL. `DECKCAPTURE_WORKER_PATH` can select an absolute DLL/executable for a controlled deployment; configured FFmpeg and FFprobe paths must resolve under the service identity.

Run the targeted tests for an affected boundary, plus the normal suite. Use isolated temporary state/cache/destination paths for supervisor, media, and delivery experiments. Do not point tests at a real archive share or the default user's retained recordings. Local delivery tests do not replace archive-server acceptance.

## Operational invariants and failure safety

Preserve source bytes and evidence. Never silently overwrite media, discard a corrupt/longer partial, publish an unverified or partial delivery, remove an undelivered source, or automatically resume an interrupted recording into its prior filename. The capture spool and finalized output may coexist and require roughly twice final media size, in addition to pre-roll, reservations, and emergency free space. The configured reservation rate is an estimate and needs measurement. Retained older packages continue consuming disk; no automatic recording-retention cleanup exists.

After supervisor restart, active recordings are marked `Interrupted` and are not resumed. Preserve their `spool-*`, `.partial`, worker logs, timing and validation records; investigate closed segments under the capture procedure and use a new recovery filename. In-progress deliveries become retryable and use validated partial-prefix resume. Preserve failed delivery staging and source packages until investigated. Shutdown requests graceful finalization and cancels active delivery tasks; a hard crash is not a successful finalize or delivery.

Use process argument lists, never shell-built command strings. Keep destination traversal confined to configured roots and reject reparse-point paths. Configure ACLs to prevent untrusted directory replacement while service checks are running. Same-root atomic directory rename, SMB durability, credentials, reconnect behavior, and storage-admin trust still require deployment testing.

Never install/register a service, change firewall rules, start real deck transport, send real email, or remove retained media as a development test without applicable authorization. Prefer synthetic sources, unique temporary directories, and inert configuration. SMTP test dispatch is a real queued message and requires an explicit user action.

## Checklist for future coding agents

Before changing code:

- [ ] Read `AGENTS.md`, the relevant design/contract documents, and the nearest component README.
- [ ] Identify the owning boundary. Keep shared models and API behavior coordinated with the Supervisor integrator.
- [ ] Check the current runtime implementation and persisted model before treating design-preview behavior as real functionality.
- [ ] Preserve the pilot readiness limits above. Do not claim DeckLink or archive acceptance without recorded evidence from the actual target environment.
- [ ] Avoid exposing tokens, SMTP secrets, personal metadata, local media paths, or retained test recordings in logs, exports, docs, or tool output.

Before handing off:

- [ ] Update the component and operations docs when behavior, API, recovery, configuration, or deployment requirements change.
- [ ] Keep tests synthetic and isolated; retain failure evidence. State precisely which tests ran and which are opt-in or still require hardware/archive acceptance.
- [ ] For capture changes, inspect package timing, audio/video streams, frame counts, preview behavior, and failure leftovers. Do not equate FFprobe validation with full QCTools or preservation conformance.
- [ ] For delivery changes, exercise corruption, cancellation, retry, path boundaries, and no-overwrite behavior. Do not report Delivered until every destination artifact is revalidated.
- [ ] Verify publish output includes the complete worker runtime and UI. Do not report a deployable installer or production readiness when only the framework-dependent publish directory exists.
- [ ] Do not clean `.local`, `artifacts`, state directories, spool trees, partial copies, or delivered packages without a specific retention decision and authorization.
- [ ] Report limitations, actual verification results, and unresolved hardware/storage acceptance work plainly.

## Acceptance work before production

1. Validate hardware identity, DeckLink-enabled FFmpeg/driver versions, each requested video mode and 10-bit path, embedded audio order/depth, signal loss, format change, drop detection, and source timecode behavior.
2. Measure source-to-file A/V sync, trigger/pre-roll interval, duration error, sustained per-channel throughput, simultaneous capture, preview load, spool I/O, and transfer verification. Capture the evidence with known reference sources.
3. Test power loss, full disk, worker/supervisor crash, restart reconciliation, corrupt spool/final media, interrupted copy, and verified resume. Confirm no original bytes are overwritten or silently removed.
4. Validate the actual archive share under the production service identity: UNC permissions, disconnect/reconnect, same-root atomic rename, durability, hashes, playback, and storage capacity.
5. Establish protected HTTPS certificate and token provisioning/rotation, Windows service identity and ACLs, restart/shutdown behavior, backup/restore of SQLite plus Data Protection keys, secret recovery, logging, monitoring, incident handling, and explicit media-retention policy.
6. Validate preservation requirements independently, including FFV1 v3/CRC claims, A/V continuity, ancillary data, source timecode, captions, and any required QCTools report. Do not treat current `qc-summary.json` as a substitute for this acceptance.
