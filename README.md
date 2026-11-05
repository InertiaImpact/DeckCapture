# DeckCapture

DeckCapture is a .NET 10 pilot for supervising tape capture to local media spools, validating completed files, and delivering verified packages to configured storage. The browser console is served by the supervisor. The repository includes capture workers, a synthetic source, a scheduled delivery engine, persistent job state, and operational tests.

DeckCapture handles capture and delivery operations. It does not provide permanent archive cataloging, collection search, or media retrieval; those remain with the station's archive systems.

## Configuration and diagnostics

Configuration uses labeled forms for channels, storage, capture, detection, transfer schedules, metadata and SMTP. In **Configuration / Channels & inputs**, choose **Discover inputs** to enumerate the host's available FFmpeg DeckLink sources. Choose a discovered input or enter its exact name, then list formats while all channels are idle. Synthetic channels are never hardware discovery results. See [input setup](docs/operations/INPUTS.md).

The **Logs** tab provides a development logging switch, recent structured logs and an authenticated diagnostic-bundle download. On another capture PC, use that PC's protected HTTPS interface and access token to control channels and inspect logs. See [diagnostics](docs/operations/DIAGNOSTICS.md) for redaction, retention and network setup.

## Current readiness

- The supervisor defaults to four synthetic channels and a local test destination. Configuration is persisted on first run; editing a file example alone does not configure a running installation.
- Capture uses FFmpeg segment spooling and FFprobe validation, with FFV1 v3 / 10-bit 4:2:2 video and 48 kHz 24-bit PCM audio settings. Timing is segment-based and is not frame-accurate at the trigger boundary.
- DeckLink input requires a compatible DeckLink-enabled FFmpeg build and Desktop Video driver. Test the actual card, source formats, timing and sustained station workload; a clean build or passing synthetic tests does not establish hardware readiness.
- Completed QC summaries contain FFprobe validation results and warnings. Optional offline QC can run a full-file FFmpeg analysis and emit native DeckCapture JSON measurements; this is not a QCTools XML report, calibrated scope, or full preservation-conformance workflow.
- The supervisor has Windows Service hosting support, but this repository does not install/register the service or configure certificates and production ACLs. Publishing is framework-dependent by default; `-SelfContained -Runtime win-x64` includes the runtime for supervisor and worker. It does not create a signed installer.
- `/api/health` confirms that the HTTP endpoint responds. It does not certify capture hardware, free-space admission, archive connectivity, or production readiness.

## Quick start on Windows

Install the .NET 10 SDK selected by `global.json` (10.0.303), FFmpeg, and FFprobe. Confirm that the configured FFmpeg can encode FFV1 and that `ffprobe` is available to the application identity. Then run:

```powershell
dotnet build DeckCapture.sln -m:1 -nr:false
.\scripts\Start-DeckCapture.ps1
```

Open [http://127.0.0.1:5278](http://127.0.0.1:5278). The starter script runs the supervisor in the foreground, binds to loopback by default, and stores operational data under `%LOCALAPPDATA%\DeckCapture`. Stop it with Ctrl+C. See the [operations runbook](docs/operations/RUNBOOK.md) for state locations, configuration, LAN security, and failure handling.

To choose a different state directory or loopback port:

```powershell
.\scripts\Start-DeckCapture.ps1 -StateDirectory 'D:\DeckCaptureState' -Urls 'http://127.0.0.1:5278'
```

For protected LAN access, use an HTTPS URL, configure a valid Kestrel certificate, and set `DECKCAPTURE_ACCESS_TOKEN` to a secret of at least 24 characters before starting. See the runbook before exposing the service.

## Build, test, and publish

```powershell
dotnet build DeckCapture.sln -m:1 -nr:false
.\scripts\Test-DeckCapture.ps1
.\scripts\Publish-DeckCapture.ps1
```

The test script runs both .NET and browser module tests; install Node.js 18 or later for the latter. The FFmpeg media integration tests remain opt-in. To include them, build the worker and install compatible FFmpeg/FFprobe first, then run `.\scripts\Test-DeckCapture.ps1 -IncludeMedia`. Test media is intentionally retained under `.local\capture-tests`; review and remove it manually only when the evidence is no longer needed. The publish output is `artifacts\DeckCapture` by default and requires the .NET 10 ASP.NET Core runtime on its host.

## Guides

- [Operations runbook](docs/operations/RUNBOOK.md) — startup, state protection, capture/delivery operations, and recovery.
- [Maintenance guide](docs/architecture/MAINTENANCE.md) — ownership, safe changes, checks, and future-agent checklist.
- [Implementation status](docs/IMPLEMENTATION_STATUS.md) — implemented features and remaining hardware/deployment acceptance.
- [Capture operations](docs/operations/CAPTURE.md) — worker layout, FFmpeg/FFprobe details, timing limits, and acceptance work.
- [Delivery operations](docs/operations/DELIVERY.md) — verification, retry, schedules, and package safety.
- [Offline QC](docs/operations/QC.md) — analysis measurements, resource limits and interpretation.
- [Browser console notes](docs/operations/WEB.md) — UI/API assumptions and manual checks.
- [Architecture contracts](docs/architecture/CONTRACTS.md) — interfaces, API routes, and shared ownership.
- [Design and implementation plan](docs/DESIGN_AND_IMPLEMENTATION_PLAN.md) — system goals and remaining acceptance requirements.
- [Interactive UI preview](docs/preview/index.html) — a design simulation only; it is not connected to the runtime.
- [Actual console screenshot](docs/images/deckcapture-console.jpg) — prepared synthetic input with live preview and sampled monitoring views.
