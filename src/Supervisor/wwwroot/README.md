# DeckCapture web UI

The browser UI is plain JavaScript ES modules with no build step or runtime CDN dependency. `index.html` loads `app.js`; the local HLS player and its license are shipped beside it.

## Modules

- `app.js` bootstraps auth, state/event updates, and user actions. It connects the views and policy modules to the page.
- `api.js` owns HTTP requests, session-only bearer tokens, event streaming, and request idempotency.
- `shared.js` contains small HTML and display helpers used by feature views.
- `capture/view.js` renders channel detail and overview, preserving setup fields, channel rows, preview media, and player controls across state refreshes.
- `capture/metrics.js` formats signal, analysis freshness, integrity warnings, and up to two measured audio-channel RMS values.
- `capture/controls.js` builds capture commands and checks the current channel, job, state, and revision before a command is sent.
- `capture/start-request.js` tracks pending browser requests per channel across rendering changes; it never owns recording state or timers.
- `capture/folders.js` owns the native destination-folder dialog, abortable reads and draft navigation. Its dialog lives outside the refreshed capture panel. Only Use folder commits; close/invalidated channel, destination or recording context must discard selection and stale responses. Keep names escaped, focus restored and approved-root checks on the server.
- `capture/player.js` owns client-side HLS playback. It scopes media requests to the current same-origin channel/session, attaches credentials only to those requests, starts muted, and tears down local media without changing capture state.
- `capture/adapters.js` connects detail and overview video elements to the shared player while retaining same-session playback and controls.
- `capture/preview.js` manages authenticated JPEG preview refresh, decoded image URLs, thumbnails, and stale-request cleanup.
- `config/view.js` renders configuration controls and DeckLink card/source selection. `config/input-map.js`, `config/structure.js`, and `config/controller.js` hold mapping projection, advanced-editor synchronization, and form updates.
- `jobs/view.js` renders the transfer queue, QC results, and diagnostics log.

## Lifecycle rules

- State refreshes update existing channel, setup, preview, and player nodes where possible so typing and playback continue.
- The detail preview's recording strip uses supervisor `recordedSeconds` and the current job duration, including extensions. Never derive it from delayed preview media or a browser clock. Patch the strip inside the retained preview card on every snapshot; waiting/starting/finalizing must not appear as active red Recording. Detail and Overview audio use native video controls and shared audio arbitration. Auxiliary audio buttons and their action handlers are removed; keep native volumechange ownership and stale-player guards covered when cleaning up UI.
- The server owns automatic input monitoring and recording. Opening or closing a view, changing a tab, muting, pausing, or detaching a player only affects browser media; it never starts or stops an input.
- Overview commands carry the clicked channel, job ID, and revision. The current snapshot is checked before a recording command reaches the API.
- Preview requests use bearer headers, never URL credentials. HLS playlist and segment requests must remain inside the current session URL. A changed token, session, tab, or mounted view invalidates pending work.
- Missing or stale analysis and audio samples display as unavailable. Meter values are measured per input channel and are RMS readings, not peak or clipping measurements.
- Channel-to-connector highlighting uses reported SDK evidence or explicit operator mappings. It does not infer physical wiring from device order.
- Configuration edits remain drafts until validation and save. Passwords are sent to the separate secret endpoint and are excluded from configuration JSON.

## Focused checks

The modules are directly importable by Node using the local `package.json` module setting. From the repository root, run focused browser-policy checks with `node tests/<name>.test.mjs`; the test files cover capture lifecycle, media authorization and cleanup, analysis display, channel mapping, settings, and config draft synchronization.

## Recording setup units and state

Duration inputs and segmented buttons display minutes. Drafts and API requests keep integer seconds; round fractional minutes once when converting. Snapshot/restore helpers must convert in both directions. Only an operator edit or preset/Custom click marks `duration-touched`; an untouched setup follows a changed default. Duration event handlers must look up the current draft by channel each time: SSE reconciliation replaces draft objects, so closing over a prior object loses later edits and Custom state.

The Start recording action sends one prepare request with `startImmediately:true`; Wait for content sends `waitForContent:true`. An ambiguous response error requires reading authoritative state, not preparing a second job. Existing Prepared/Waiting jobs use their revisioned Start/Cancel commands. The main recording counters use the recording-specific observations, including zero while waiting for the first post-Begin metric, never a fallback to lifetime input counters. Detected format is observed metadata, never the configured `auto` text. Detailed luma/capability/device diagnostics stay out of the operator capture view.

Start requests use the channel-scoped registry rather than the global configuration/action busy flag. Keep its pending label and disabled controls through the server's intermediate Prepared state; that state can precede input readiness by several seconds. Authoritative Recording releases the pending state even before the HTTP response arrives, so Stop is available. Waiting settles only once its recording input is Live, not while Opening. A late response may clear only its own request ID, and a failed new preparation must not mistake the channel's old terminal job for a new accepted recording. Tests import this module directly and cover those races.

`Validating` is background QC after capture input ownership has ended. It does not lock the selected channel's recording setup or masquerade as a live capture. The selected channel and Recent jobs show reported `qcProgress` phase, processed frames, media time, and analysis percentage only when both measured durations are available. Older snapshots without progress show that detail is unavailable; old timestamps are displayed without inferring a stall.

Recording setup failures use capture/feedback.js, scoped to the submitting channel. Server validation remains authoritative; message routing uses the submitted values to distinguish filename and destination-folder name failures. Field messages use textContent, role=alert, aria-invalid and aria-describedby; unknown errors appear beside setup actions. Focus/scroll occurs only when a new error arrives on its original visible channel. Reapply feedback after setup reconciliation without replacing live controls or repeatedly announcing unchanged errors; clear the corresponding message on edit, preset selection or folder choice. Global notices remain for other views.
