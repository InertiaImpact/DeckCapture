# DeckCapture operations runbook

This runbook describes the current pilot implementation. It is not a hardware commissioning sign-off or an archive deployment approval. DeckCapture supervises capture and verified delivery; it does not catalog permanent archive holdings, provide collection search, or retrieve archived media.

## Requirements and current limits

- Windows host with the .NET 10 runtime; use the .NET 10 SDK for builds and the version pinned by `global.json` for development.
- FFmpeg and FFprobe available to the supervisor's Windows identity. The capture configuration provides executable paths; the defaults are `ffmpeg` and `ffprobe` resolved through `PATH`.
- The supervisor's default FFmpeg on the development workstation supports the synthetic source but does not expose DeckLink input. A DeckLink-capable FFmpeg build, card and driver setup, source-format tests, sync checks, and workload measurements are separate acceptance work.
- Current capture uses one-second FFV1 v3 / 10-bit 4:2:2 / 48 kHz 24-bit PCM Matroska segments. It is a segment-spool feasibility adapter, not frame-accurate native capture. The exact source frame at a trigger is not guaranteed.
- FFprobe validates the output container, streams, codec/profile properties, positive duration, and decoded frame count. Optional `OfflineQcEnabled` runs a subsequent full-file FFmpeg analysis and records a native DeckCapture JSON report. It is not a QCTools XML report, calibrated broadcast scope, or preservation certification.
- The default development configuration contains four synthetic channels and a `local-test` destination. It is suitable for a synthetic workflow only; it is not a real deck or archive destination configuration.

Read [CAPTURE.md](CAPTURE.md) and [DELIVERY.md](DELIVERY.md) for component constraints before accepting real media.

## Start and verify the supervisor

From the repository root, after installing .NET 10, FFmpeg, and FFprobe:

```powershell
dotnet build DeckCapture.sln -m:1 -nr:false
.\scripts\Start-DeckCapture.ps1
```

The script runs the supervisor in the foreground at `http://127.0.0.1:5278` by default. Open that address in a browser. Ctrl+C requests graceful shutdown. The program also has Windows Service hosting support, but no service registration/installer script is supplied; service identity, working directories, certificate, ACL, restart, and shutdown policy must be configured by the deployment owner.

Verify the HTTP host:

```powershell
Invoke-RestMethod http://127.0.0.1:5278/api/health
Invoke-RestMethod http://127.0.0.1:5278/api/state
```

`/api/health` returns a minimal HTTP-ready status. It does not prove FFmpeg can open a device, admit a recording, or reach a remote destination. Use the browser's capability panel and `/api/state` to inspect the current probe and channel/job state. A synthetic source may be available while DeckLink support is unavailable.

When `DECKCAPTURE_ACCESS_TOKEN` is configured, include `-Headers @{ Authorization = "Bearer $env:DECKCAPTURE_ACCESS_TOKEN" }` on the `/api/state` request. `/api/health` remains unauthenticated.

The supervisor takes an exclusive lock on its state directory. Do not run two instances against the same state directory. On restart, jobs that were prepared, waiting, recording, finalizing, or validating are marked `Interrupted`; capture does not resume automatically. In-progress deliveries are scheduled for a safe retry.

## State, configuration, and secrets

Default state location: `%LOCALAPPDATA%\DeckCapture`. Override it before launch with `DECKCAPTURE_STATE_DIRECTORY` or use the starter script's `-StateDirectory` argument. The directory contains:

- `deckcapture.db` and SQLite WAL files: configuration, job history, delivery state, idempotency results, and notifications.
- `keys\`: ASP.NET Data Protection keys (DPAPI protected on Windows).
- `smtp.secrets.json`: protected SMTP credential data when configured.
- `media\`: by default, cache root containing job packages and `.spool` capture data.
- `delivered\`: the default local test destination, separate from the cache.
- delivery checkpoints/other runtime records as created by the supervisor.

Protect and back up the database, protection keys, SMTP secret file, retained media, and any other required state together. A database copy without its keys may make protected SMTP credentials unavailable. Use a local fixed volume for supervisor state and capture spool. The configured delivery destination may be a local path or UNC path, but must be an approved absolute root separate from and non-overlapping with the cache. Configure the Windows service identity with the required access to FFmpeg, the local cache, destination, and certificate.

The first start writes `ConfigurationRules.DevelopmentDefaults` to the database if no configuration exists. Changes must be made through the configuration UI/API or deliberate state migration; changing `docs/settings.example.json` does not update the persisted runtime configuration. Configuration changes are validated. A channel with an active or prepared job cannot be remapped or removed. Jobs retain configuration snapshots; the global experimental end-detection disable is an intentional kill switch that also cancels pending heuristic-stop timing.

The service binds to loopback at `http://127.0.0.1:5278` by default. To choose a local URL, use `-Urls` with the starter script or set `ASPNETCORE_URLS`. For example:

```powershell
.\scripts\Start-DeckCapture.ps1 -Urls 'http://127.0.0.1:5280'
```

LAN binding is rejected unless every configured URL uses HTTPS and `DECKCAPTURE_ACCESS_TOKEN` is set to a secret of at least 24 characters. Configure a trusted Kestrel HTTPS certificate through normal ASP.NET Core Kestrel configuration before using a LAN URL; the URL alone does not provision a certificate. Example URL:

```powershell
$env:ASPNETCORE_URLS = 'https://0.0.0.0:5278'
$env:DECKCAPTURE_ACCESS_TOKEN = '<deployment-managed secret of at least 24 characters>'
.\scripts\Start-DeckCapture.ps1 -Urls $env:ASPNETCORE_URLS
```

Do not commit, log, or put the token in a URL. Store it in the deployment's protected secret mechanism and supply it to the service identity. API clients send `Authorization: Bearer …`; the browser uses tab-session storage and authenticated fetch for the event stream and preview. Mutations also require same-origin JSON and an idempotency key. `/api/health` is excluded from the token requirement and is intentionally minimal.

SMTP host, username, recipients, and TLS mode are configuration. In Configuration → SMTP & reports, enter the credential in the separate password field and choose **Save credential**; the UI sends it to the authenticated `/api/secrets/smtp` endpoint, clears the field after success, and never puts it in configuration JSON or export. The SMTP password is stored separately with Data Protection. A test notification queues a real message only after an explicit UI/API request. Queue acceptance does not mean the remote server accepted or delivered it.

## Configure and operate a recording

In **Configuration → Channels & inputs**, compact rows retain configuration order. Choose **Discover inputs** to refresh the host's actual FFmpeg endpoints and optional physical-card metadata. A row's input choice saves the exact device key; editing labels or selecting a picture does not create a new hardware identity. Validate and save to persist changes. Refreshing discovery, hovering/focusing rows, and selecting diagram endpoints only change observation/highlighting. They never remap a channel automatically.

Focusing or hovering a DeckLink row highlights its assigned endpoint. Selecting an endpoint highlights every matching row, so duplicate draft assignments are visible; the server refuses saving two channels on the same DeckLink key. Synthetic rows are separate test sources. A saved name missing from discovery remains visible as unavailable/manual: refresh after driver/profile/hotplug changes and deliberately select the correct input. Do not replace it by array order or assume another similarly named endpoint is the same jack.

Automatic card groups require a reported nonzero SDK DeviceGroupID and exact endpoint matches. The optional `media-tools/DeckCapture.DeviceInventory.exe` needs Desktop Video on the operating PC, not SDK/compiler. Documented diagrams cover original Duo/Quad, Duo 2/Quad 2 and 8K Pro; only known SDK model/profile/SDI evidence produces automatic assignments. A DeckLink SDI label alone does not tell you whether the endpoint belongs to a standalone card, original Duo or original Quad.

For older/unknown hardware, use the physical wiring editor to choose a documented model or define a custom ordered connector layout, label the physical card, and associate verified input jacks with exact endpoint keys. Validate and save: these operator assertions persist separately from channel capture assignments and never change a driver profile. Outputs and reference cannot be mapped as inputs. SDK grouping wins conflicts. A single operator diagram may annotate an unknown SDK layout only when all its present endpoints belong to that one group and no verified SDK layout/assignment exists; it keeps the SDK group ID. Mixed-group and conflicting entries remain unapplied, and absent saved endpoints remain visible instead of being reassigned. Refresh after cabling/profile/hotplug changes and deliberately review the annotations. See [the input guide](INPUTS.md#diagram-coverage-and-evidence) for supported templates, authority and commissioning limits.

Keep prepared/armed/recording/finalizing inputs unchanged. The server also protects an input while its open operation is still draining after cancellation. Remap an idle channel only after its job is closed; editing an unrelated idle channel does not alter the active job's immutable snapshot. Listing formats is an explicit separate action and requires every capture input to be idle. A device list, profile or format table does not prove SDI lock, source timing, 10-bit precision, throughput or hardware readiness.

1. Start with synthetic defaults and confirm a synthetic preview/metrics update. Do not select or promise DeckLink capture until the exact installed FFmpeg build reports DeckLink support and the hardware-specific acceptance plan has passed.
2. Configure channels, unique physical inputs, local cache root, destination root, time zone, transfer windows, and rates. The validator permits 1–16 configured channels and 1–16 concurrent transfers, but these are configuration bounds, not measured workstation capacity.
3. Set metadata visibility and required flags. Hidden metadata is omitted from new jobs. Required fields must be visible and supplied. Existing jobs retain their earlier metadata snapshot.
4. Select the channel, enter a unique safe filename, choose a duration in minutes and an approved destination folder. Configuration / System & storage selects the default duration preset. Only one prepared, waiting, recording or finalizing job is admitted per channel.

Browse opens a floating destination-folder dialog. Navigate folders and Up one folder, then choose Use folder to commit the selection. Cancel or Escape leaves the previous destination folder unchanged. Background live updates do not replace an open dialog; a changed channel, destination or active recording closes it safely.

5. Choose **Start recording** for immediate capture after input/storage checks, or **Wait for content** to arm the existing content detector. Immediate start is one accepted server-owned request, so closing the browser does not strand its preparation step. Waiting still requires a non-content observation followed by confirmed picture content; its armed-time limit remains separate.
6. The normal UI does not need a separate Prepare step. A legacy or safely retained Prepared job still offers Start and Cancel; an uncertain worker-start failure remains failed with evidence retained. Duration starts at the actual recording trigger and pre-roll is additional. Extend adds time to an active recording, up to 12 hours; Stop finalizes, and Cancel applies to prepared/waiting jobs.

Starting a recording currently releases the idle monitor and opens a recording worker on the same input; that handoff can take several seconds and the preview may briefly disappear. The recording timer begins after the input is ready, rather than at the browser click. With development logging enabled, capture entries identify the job and report acceptance, prior-monitor release, input readiness and recording acknowledgement durations. Use those phases to distinguish an input-open delay from an unacknowledged recording command; a successful HTTP response alone does not establish successful finalization.
7. Experimental end-of-tape detection is globally disabled by default. Keep it off unless a deck-specific acceptance plan authorizes it. Its black/silence metrics are sampled and are not a calibrated tape-end diagnosis.
8. In Configuration → Capture profile, enable **Run optional offline QC after validation** only when the expected scan time and additional FFmpeg disk reads are acceptable. It is off by default and runs after capture finalization, before the package is released to delivery. The analyzer is serialized to one job per supervisor process, runs FFmpeg at low process priority, and reads the first video and first audio stream. Its current code defaults are a six-hour timeout and 2,000 reported intervals; these are not yet runtime settings. If the configured tools or required filters fail, finalization may fail and the package will not enter delivery. Review `qc-summary.json` warnings and thresholds as indicators only; static pictures, leader, and intentionally silent content can be legitimate. This is not QCTools XML, calibrated scopes, or pass/fail preservation certification.
9. After finalization, review warnings or `NeedsReview` jobs. The QC summary contains FFprobe validation, measured duration/frame/drop counts, supervisor warnings, and optional analysis when enabled. Reviewing a valid `NeedsReview` job releases it to delivery admission.

The supervisor reserves an estimate based on configured bytes/second, requested duration, pre-roll, and a factor of two for spool and finalized output, while retaining the configured emergency reserve (1 GiB by default). This is an admission estimate; it is not a measured guarantee of encoded size. Retained earlier media also consumes disk. Do not lower the emergency reserve or reservation estimate without measurements and a rollback plan.

## Delivery operations

The supervisor queues a finalized package only after local media validation and artifact hashing. Jobs awaiting review create a held delivery. Use the queue to hold/release, retry, and apply the time-limited per-file or global override. The global override lasts exactly 12 elapsed hours. Overrides change schedule admission only; concurrency, validation, path boundaries, and verification still apply. A transfer already running slows to the outside-window rate when the window closes. A reconnected retry needs schedule admission again.

The delivery engine copies to a private staging folder under the destination root, validates retained prefixes before resume, hashes all destination artifacts, writes a completion manifest, then publishes by same-root directory rename. Existing files/packages are never overwritten. Confirm the final destination package and `Delivered` state from server snapshots; UI progress or a partial copy is not proof of delivery.

The actual archive server, SMB credentials/service identity, atomic rename behavior, reconnect handling, storage durability, and sustained rate must be accepted on the intended destination. Software tests on a temporary local directory do not prove those properties.

## Failure handling and retained media

**Do not delete spool data, `.partial` files, validated media, failed delivery staging, or the local source package to clear an alert.** The application intentionally retains local recordings and error evidence; it has no automatic retention cleanup. Unfinished package bytes are never silently replaced.

- **Capture input cannot open:** leave the job failed and inspect the channel's capability/error details, configured FFmpeg path, device assignment, and service identity. Do not retry repeatedly against an unverified physical input.
- **Warning, dropped-frame report, or `NeedsReview`:** preserve the original package, inspect job warnings and `metadata.json`, `qc-summary.json`, `validation.json`, `timing.json`, and `encoder.log` where available. Validate any recovery output under a new filename. Do not mark it reviewed until an operator has evaluated it.
- **Supervisor restarted during capture:** the job is `Interrupted`, not resumed. Preserve its `.spool` tree and package. Review worker logs and surviving closed segments under the capture procedure; any remux/recovery must create a new filename and pass validation and operator review.
- **Transfer failed or paused:** keep the source package and destination `.deckcapture-staging` directory. Inspect the recorded error and verify that no transfer is active before any manual staging action. Retry only through the supervisor so prefix and hash checks run. A corrupt or unexpected staging package is evidence for investigation.
- **Low cache space:** stop new admissions, account for reserved bytes and all retained local packages, and expand or deliberately manage storage after review. Never remove an undelivered source package. Filesystem exhaustion can stop a capture.
- **SMTP failure:** capture and delivery remain independent. The notification outbox retries; inspect notification status/error and SMTP TLS/credential configuration. Do not resend manually until checking whether the server accepted the original message.
- **Configuration/token exposure:** restrict the service to loopback immediately or stop it; rotate the access token in deployment-managed storage. Do not put credentials in issue text or exported configuration.

Before moving/removing retained media, record the job ID, state, filenames, hashes, and reason; confirm no worker or transfer holds the files; preserve an evidence copy under the site's media incident procedure; and get the responsible operator's approval. No cleanup command is provided.

## Verification commands

```powershell
.\scripts\Test-DeckCapture.ps1
.\scripts\Test-DeckCapture.ps1 -IncludeMedia
.\scripts\Publish-DeckCapture.ps1
```

The default suite exercises configuration, schedule, path-safety, and delivery behavior. `-IncludeMedia` opts into FFmpeg/FFprobe synthetic integration tests; they retain files under `.local\capture-tests` and do not validate DeckLink hardware. The publish script creates a framework-dependent output under `artifacts\DeckCapture` by default and checks for the supervisor, static UI, and capture worker files. Install the .NET 10 ASP.NET Core runtime on the target host. The script does not install the service, configure firewall rules, enroll a certificate, or validate production hardware/storage.
