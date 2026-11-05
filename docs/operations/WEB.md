# DeckCapture browser console

The supervisor serves `src/Supervisor/wwwroot/index.html` as the live console. The client is plain HTML, CSS, and ES modules; it has no build step, remote assets, or runtime dependencies.

## API assumptions

The client follows `docs/architecture/CONTRACTS.md` and the runtime JSON models in `src/Domain/Models.cs`:

- JSON properties use camel case and enum values are strings such as `Prepared`, `Recording`, and `Held`.
- `GET /api/state` supplies the configuration, channel/job/metrics snapshots, recordings, deliveries, alerts, notifications, and system status. The page does not create capture state or run a recording clock.
- `GET /api/events` emits `state` events containing complete snapshots. Without a token the client uses `EventSource`; with a token it uses fetch streaming so the `Authorization: Bearer` header is available. It never puts the token in a URL. It reads `/api/state` after a stream reconnect.
- Recording preparation uses `POST /api/recordings/prepare`. Start, stop, cancel, extend, and review pass the job's current `revision`. A 409 is shown and triggers a fresh state read.
- Mutations send JSON and a new `Idempotency-Key`. Errors, validation results, and stale revisions remain visible in the page notice.
- Configuration reads and writes use `GET /api/configuration`, `POST /api/configuration/validate`, and `PUT /api/configuration`. The editor exposes channel I/O, capture profile, detection, transfer windows and rates, metadata visibility, and SMTP/report settings as individual JSON sections. Metadata visibility/required toggles and `capture.offlineQcEnabled` are also available as checkboxes. The experimental end-detection master switch is separate and clearly labeled. An SMTP password is entered separately and sent only to `POST /api/secrets/smtp`; it is not loaded into configuration or retained in browser storage. SMTP test dispatch requires an explicit click and confirmation.
- Folder navigation uses the destination-scoped folder API and passes only the relative path. Preview frames are fetched from the channel preview endpoint and displayed only when the server returns an image. Metrics are rendered only from the supervisor snapshot; no fabricated scopes or sample values are generated.

The contract does not specify a report-kind enum. Report preview values are sent as the documented `kind` string. QC results render the server's FFprobe summary and any optional offline-analysis measurements and intervals in the QC/jobs panel.

## Modules

- `wwwroot/api.js` owns authenticated requests, idempotency headers, error decoding, token storage, and the state event stream.
- `wwwroot/app.js` renders capture, queue, QC/jobs, and configuration panels and dispatches explicit operator commands. Its luma profile plots only the worker-reported 64 column means from one reduced frame; its audio meter displays only the reported sampled mono RMS. QC details show FFprobe validation and optional DeckCapture offline-analysis measurements/intervals. These views are not QCTools or calibrated scopes.
- `wwwroot/styles.css` contains the compact console styling, responsive layout, and visible focus states.

The access token is kept in `sessionStorage` for the current tab session. Clearing the field and applying removes it. Browser storage is not used for job state or configuration.

## Manual smoke checks

1. Open the supervisor root page on loopback. Confirm the channel list and capture metrics match `/api/state`; with no available preview, confirm the empty preview message is shown.
2. Select each channel and confirm its current job and metrics change to the selected server snapshot.
3. Prepare a recording with a visible metadata field, select a configured destination, and browse approved folders. Confirm the prepare response/state appears and the job revision is shown.
4. Start, extend, stop, and cancel a suitable test job. Confirm commands use the displayed current revision and subsequent state comes from the supervisor. For a stale revision, confirm the conflict is visible and state refreshes.
5. Arm a job only against a source configured for real content detection. Confirm the client reports the server job state without inventing a trigger or timer.
6. Hold/release and retry an eligible delivery. Exercise the per-file override and the global 12-hour override; confirm queue state/rates come from server snapshots.
7. Review a completed job's QC endpoint and release a `NeedsReview` job. Confirm the server FFprobe summary and optional offline-analysis measures/intervals are rendered without simulated results.
8. Edit each configuration section, validate, and save a safe test configuration. Toggle metadata visibility/required flags and optional offline QC. Confirm secrets do not appear in loaded/exported JSON.
9. Save a test SMTP credential through the separate password field. Confirm it clears on success and never appears in configuration or export. Verify SMTP test remains idle unless explicitly clicked and confirmed.
10. Enter an access token in a separate browser tab and apply it. Confirm API and SSE requests are authenticated without a token query parameter; clear and apply it to remove it from that tab session.
11. Use keyboard Tab and arrow keys through the main tabs, then check at narrow mobile width that controls stay usable and no page-level horizontal overflow appears.
