# Optional offline media QC

## What runs

`OfflineQcService.AnalyzeAsync(mediaPath, captureProfile, options, cancellationToken)` is a read-only, asynchronous service. The supervisor may opt in with `Capture.OfflineQcEnabled`; it is disabled by default to protect capture throughput. The service has no HTTP endpoint that accepts an executable path. It uses the application's configured FFmpeg/ffprobe paths. Calling code owns job admission, review policy, persistence and publishing the returned report before the package checksums are sealed.

The service probes container and stream metadata using ffprobe, then performs one full FFmpeg decode of the first video and first audio stream. It does not run a redundant ffprobe `-count_frames` decode: capture finalization already performs that validation, and the QC pass must decode again to analyze all video frames/audio samples. `-xerror -err_detect explode` makes decoder errors fail the analysis. The FFmpeg progress frame counter is returned as `decodedFrames`, and the signalstats frame count is separately returned for comparison. Additional streams are listed by the probe, with a warning that they were not decoded.

Available filters are detected from the configured FFmpeg build. Missing filters are reported explicitly; missing evidence is not a zero measurement. The analysis uses:

- `signalstats` plus `metadata`: per-frame luma/chroma/saturation statistics, frame differences, temporal outliers, repeated-line and broadcast-range estimates, accumulated into count/minimum/maximum/mean values.
- `blackdetect`: black intervals, using configurable pixel/picture thresholds and minimum duration.
- `silencedetect`: silence intervals for each channel in the first audio stream; audio-channel indices are zero-based.
- `freezedetect`: approximately frozen-picture intervals, using configurable noise and minimum duration.

The native report identifies itself as `deckcapture.offline-qc.v1`. It contains source size and optionally SHA-256, format/stream information, decoded and analyzed frame counts, filter availability, measures, intervals, thresholds, omissions, warnings and a bounded diagnostic tail. It is **not** a QCTools XML report and is not advertised as importable into QCTools. No qcli export or arbitrary external-command API is implemented.

## Interpreting measures and intervals

Uppercase measure names are FFmpeg `lavfi.signalstats` keys in their native units. For example, `YMIN`, `YMAX`, `YAVG` and `SATMAX` remain source integer code values, with a count/minimum/maximum/mean across analyzed frames. Frame means are weighted equally, not by duration. `YBITDEPTH` describes occupied sample bits; it must not be read as the source storage bit depth. Source storage depth comes from ffprobe's codec/pixel-format information.

Additional `lumaMinimumNormalized`, `lumaMaximumNormalized` and `lumaAverageNormalized` measures divide integer luma by `2^storageDepth−1`. `saturationMaximumNormalized` and `saturationAverageNormalized` divide FFmpeg saturation by `sqrt(2)×2^(storageDepth−1)`. These are code-value measurements, not calibrated brightness, nits, colorimetric saturation or legal-range certification. Converting RGB or an unsupported pixel format for signalstats can change interpretation; the report warns about non-YUV input. HDR, transfer functions, chroma subsampling, interlaced field timing and deck-specific analog behavior require separate calibration.

`lowLuma`, `highLuma` and `highSaturation` intervals identify consecutive frames whose minimum luma, maximum luma or maximum saturation crosses the configured normalized threshold. Defaults are luma below .05, luma above .95 and saturation above .9. Those thresholds are broad code-range heuristics, not broadcast legal-range limits. One extreme pixel can flag a frame. `peakValue` is the minimum normalized value for a low-luma interval or the maximum value for a high-luma/saturation interval. The underlying raw aggregate values and signalstats `BRNG` estimates remain available for review.

Black/silence/freeze intervals preserve FFmpeg's reported timestamp bounds. A detector still active at end of media has `openAtEnd: true`; video EOF closure estimates the last frame's end using the probed average frame rate, while audio closure uses container duration if FFmpeg did not emit an end. Variable frame rate or malformed timestamps can make that estimate imprecise. Black detection commonly reports the last black frame's presentation timestamp as the final endpoint (for a two-second, 25 fps all-black test, 1.96 seconds); do not silently reinterpret it as an exact two-second boundary. Non-monotonic frame timestamps raise a warning.

Black leader, still photographs, silent tracks and blank sections may be legitimate content. Findings do not automatically establish a defective preservation master, and absence of findings does not certify capture quality. This analysis cannot recover lost source frames, prove DeckLink lock, establish A/V synchronization or inspect captions/timecode/ancillary data. It does not calculate PSNR/SSIM without a reference, perceptual quality scores or a calibrated waveform/vectorscope.

## Resource and failure policy

All calls in a supervisor process share a static concurrency gate of one. FFmpeg uses bounded decoder threads (one quarter of available processors, minimum one, maximum eight) and filter threads (one eighth, minimum one, maximum four), with normal process priority. Background process priority can starve even tiny probes under normal preview/encoding load; CPU and I/O budgets bound work instead. Only filters supporting slice threading use that budget. Default input rate is two media seconds per wall-clock second, adjustable from .1× to 16×. This is a media-time ingestion limit, **not** a guaranteed disk MiB/s cap or protection from every storage-contention scenario. Measure simultaneous capture and QC on station hardware before assuming adequate headroom. SHA-256 adds another sequential read; callers that will hash the sealed package anyway may set `ComputeSha256=false` to avoid that extra read.

Default timeout is six hours; accepted settings bound it to one second through 48 hours. Media duration must be positive and at most 48 hours. The timeout covers probe, filter discovery, decode and optional hashing, after admission to the concurrency gate. A queued call remains cancellable while waiting. The maximum reported interval count defaults to 2,000 and is capped at 10,000. Later findings increment `omittedIntervals`; aggregate measures continue. State contains per-metric aggregates, current frame values and currently open detector intervals, never an array of every frame. Probe/filter metadata is capped at 1 MiB, the report diagnostic tail at 64 lines of 2,048 characters, and process failure detail at the latest 12 such lines.

Process arguments are separate `ArgumentList` entries. FFmpeg writes only to its null muxer and redirected pipes. The source is held open read-only with Windows sharing that denies concurrent writes/deletes. No media or report path is sent to an output muxer. Cancellation/timeout kills the process tree, drains/awaits the child exit, closes the source guard and releases the concurrency gate. A crash of the entire supervisor is a separate recovery boundary; this service does not promise a persisted resumable QC checkpoint or a Windows child-job containment policy. It never changes or repairs source bytes.

Probe/decoder/process failures throw and must not be represented as completed QC. Canceled analysis returns no partial successful report. The supervisor must retain the original media and decide whether to retry or require review. Filter unavailability, capped intervals and unsupported interpretation remain structured warnings in an otherwise completed report. Preserve those warnings when exporting/serving the report. Do not alter a package that delivery has already sealed or started copying; a later explicit QC request needs a separate sidecar/report version.

## Maintenance and verification

The QC maintainer owns filter options, log/parser compatibility, code-value normalization, EOF handling, resource bounds and tests in `QcTests`. The supervisor maintainer owns opt-in configuration, execution scheduling, cancellation on shutdown, immutable package sealing, UI/report presentation and review/delivery admission. Rerun tests and examine real report samples whenever FFmpeg is upgraded or the source profile changes. Parsers consume FFmpeg log text; unknown/new formats can cause missing counts or intervals and require review rather than a completeness claim.

Focused tests cover ten-bit normalization and frame aggregation, capped intervals with omission counts, per-channel silence, EOF freezes, option bounds and cancellation. Enable the real-media test with:

```powershell
$env:DECKCAPTURE_CAPTURE_INTEGRATION='1'
dotnet test tests/DeckCapture.Tests/DeckCapture.Tests.csproj --filter FullyQualifiedName~QcTests
```

Verification on October 31, 2026: all four focused tests passed with integration enabled, including a two-second FFV1 v3/10-bit/PCM synthetic file with exactly 50 decoded and 50 signalstats frames. Both audio channels produced silence intervals, black detection produced 0–1.96 seconds, frozen video and the deliberately configured low-luma threshold produced EOF intervals through 2 seconds, and the source SHA-256 remained unchanged. The test also cancels a running rate-limited decode and successfully analyzes the same file again, verifying resource/gate recovery. Evidence remains under the test output's `.local/qc-tests` directory.

Filter definitions: [FFmpeg's official filter documentation](https://ffmpeg.org/ffmpeg-filters.html). Runtime options are also checked against the installed tool's `-h filter=signalstats`, `-h filter=silencedetect` and `-h filter=freezedetect` help. Station-specific acceptance still needs known reference tapes, intentionally defective files, representative analog noise, multichannel audio and full-workload disk measurements.

## Background progress and input ownership

After successful mandatory master validation, the supervisor disposes the capture session before background QC and package sealing. The channel can monitor or record again while the earlier file remains Validating. Disk reservations and delivery holds remain until sealing finishes; maintenance and shutdown still treat QC as active work. Late QC completion addresses its exact job ID and cannot dispose a newer capture on the same channel.

RecordingJob.qcProgress is optional observed telemetry: phase, frames, processedSeconds, nullable durationSeconds, and UTC updatedAt. Stages are Queued, Preflight, Analyzing, Hashing, Sealing and Complete. Analysis updates come from FFmpeg progress, throttled to about once per second, and do not change command revisions. Channel and Recent jobs views show progress; stage changes and each 30 media seconds are logged. Old jobs with no readings display unknown progress. A stale reading means no recent observation; it does not prove failure or authorize delivery. Sealing may take time after the scan reaches 100%.

Restart does not resume interrupted QC. Existing active jobs become Interrupted and original media is retained for review. Do not force a restart during an older version's validation, edit its state to Complete, or rerun sealing over existing sidecars.
