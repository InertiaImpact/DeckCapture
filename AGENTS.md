# DeckCapture maintenance instructions

Read `docs/DESIGN_AND_IMPLEMENTATION_PLAN.md`, `docs/architecture/CONTRACTS.md`, and the nearest component README before changing behavior. The implementation status document distinguishes implemented behavior from hardware acceptance work. Do not turn a hardware limitation into a simulated success.

## Working rules

- Use .NET 10, nullable reference types, and explicit cancellation. Keep the browser client small and accessible. Add dependencies only for a concrete need.
- The supervisor owns persistent job state; capture workers own their input; delivery never writes into an active recording package. Keep these boundaries intact.
- A stopped browser must not stop a recording. Closing a channel must not stop another channel. Never open the same DeckLink input twice for preview and recording.
- Preserve the original bytes on errors. Never overwrite a recording, silently drop frames, mark a partial copy delivered, delete undelivered media, or silently restart into the same recording filename.
- Experimental end detection is off by default. The global disable cancels pending heuristic stops, including on active jobs. It must not disable duration timers or content-triggered starts.
- Optional metadata may be hidden individually. Hidden defaults must not leak into newly prepared jobs. Immutable job snapshots retain earlier metadata and settings.
- Overrides bypass schedule admission only. Concurrency, bandwidth, validation, holds, destination boundaries, and verification still apply. Existing transfers slow after the window closes; reconnects require admission again.
- Use UTC for persisted instants, monotonic elapsed time for duration/rate limits, and explicit time zones for schedule windows. Define ambiguous and missing DST boundaries in tests.
- Process arguments use `ProcessStartInfo.ArgumentList`; never construct shell commands from filenames, destinations, or configuration. Confine directory browsing to approved roots, including reparse-point checks.
- Do not store SMTP secrets in exported settings or Git. Default to loopback; normal remote bindings require HTTPS and an access token of at least 24 characters. The explicit `DECKCAPTURE_DEVELOPMENT_HTTP=1` development mode permits remote HTTP without requiring a token. Any configured token must still be checked, and host checks, same-origin command checks and idempotency remain enforced in that mode.

## Maintaining changes

Keep files focused. Explain invariants and non-obvious decisions near code; avoid comments that merely repeat statements. Update the API contract, operational documentation, and implementation status when behavior changes. Add meaningful regression tests for state transitions, failure recovery, schedule boundaries, path confinement, and transfer integrity. Run the documented build, test, and synthetic end-to-end checks before delivery.

When removing a feature, trace its references through detail and Overview views, action dispatch, controllers/exports, styles, test fixtures and current operating guides. Remove dead paths rather than just hiding a control. Preserve regression coverage for shared behavior that remains, especially native audio controls, session ownership and cleanup. Keep historical verification notes and design references clearly identified; do not delete retained recordings, logs, failure evidence or vendored dependencies as incidental cleanup.

Never install a service, alter firewall rules, start real deck transport, send real email, or clean up retained media as part of a development test without the applicable authorization. Synthetic sources and temporary directories should be the default test path.
