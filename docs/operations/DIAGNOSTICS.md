# Development diagnostics

Diagnostics provide a bounded operational history and a downloadable support bundle. They do not change a recording's immutable configuration, enable experimental tape-end detection, stop capture, or turn an unavailable input into a successful capture.

Verbose diagnostics are disabled by default. The setting lives independently at `<state directory>/diagnostics-settings.json` and survives service restarts. The settings API persists the new value before reporting success. Invalid or unreadable settings leave verbosity disabled and expose a concise status error. Configuration exports do not contain this toggle.

With verbosity disabled, application Information, Warning, Error, and Critical events remain available. Debug and Trace events and successful HTTP request summaries are suppressed. Enabling verbosity admits application Debug/Trace events and summaries containing only method, path, response status, and elapsed milliseconds. Requests that fail with a server error remain visible even with verbosity disabled. Query strings, HTTP headers, request/response bodies, and logger scopes are never inputs to the request summary. Raw framework HTTP request messages below Warning are suppressed in this provider.

## Operator APIs

The browser diagnostics controls use these routes:

- `GET /api/diagnostics/status`: enabled state, effective minimum level, retention bounds, and the most recent storage error.
- `POST /api/diagnostics/settings`: `{ "enabled": true }` or `{ "enabled": false }`, with an `Idempotency-Key` and the normal JSON mutation protections.
- `GET /api/diagnostics/logs`: newest recent structured entries first.
- `GET /api/diagnostics/bundle`: a ZIP download containing sanitized runtime information, capabilities, configuration, retained logs, and available capture evidence from up to ten recent known jobs.

These use the application's ordinary `/api` access policy. Default access remains loopback; normal LAN binding requires HTTPS and an access token of at least 24 characters. Setting `DECKCAPTURE_DEVELOPMENT_HTTP=1` explicitly permits remote HTTP without requiring a token. If a token is configured, the existing token check still applies. Host checks, same-origin command checks, JSON content requirements and idempotency remain enforced. Malformed listener URLs are rejected in both modes. A browser uses the existing request helper for downloads, so configured access tokens do not enter download URLs or query strings. API responses use `Cache-Control: no-store`.

For example, setting the development flag and using `http://0.0.0.0:5278` listens on the workstation's interfaces. The flag is process configuration, separate from the persisted diagnostics verbosity toggle; leaving it unset preserves the normal binding policy.

## Retention and failure behavior

Structured JSONL files rotate under `<state directory>/logs` as `diagnostics-0.jsonl` through `diagnostics-3.jsonl`, where zero is the active file. The default maximum is one MiB per file, four files total. The in-memory ring retains at most 500 entries. Each event's message and exception text are bounded, and very large unstructured input is omitted rather than retaining a clipped secret. Large escaped entries are reduced to fit a file. Restart loads only bounded tails and ignores torn or malformed records.

Log downloads use fixed identities such as `log-0`; the service never accepts a filename or arbitrary path. Reads and rotation share a lock so an active log snapshot stays consistent. Logging storage failures remain in the bounded memory ring and status rather than propagating into capture or delivery. Diagnostic file writes are best effort operational evidence; they are not a replacement for the transactional job database or a power-loss audit guarantee.

Capture evidence comes only from known job snapshots under `<that job's cache root>/.spool/<job ID>`. The allowlist is:

| Evidence ID | Retained filename |
| --- | --- |
| `encoder` | `encoder.log` |
| `timing` | `timing.json` |
| `validation` | `validation.json` |
| `worker-stdout` | `worker-stdout.log` |
| `worker-stderr` | `worker-stderr.log` |

Only files that actually exist are included. Each evidence read takes at most the final 128 KiB and drops an initial incomplete line. The recording ID must be a GUID in its exact compact form. Worker evidence can remain useful after a failed capture even when verbose supervisor logging is off. Evidence does not include recordings, spool media, private worker options, the database, protected SMTP secrets, or data-protection keys. A missing or unsafe capture directory adds a short unavailable note to the bundle rather than breaking recording or exposing a path.

The service also validates custom retention limits: disk files, memory count, entry size, evidence size, and job count have finite bounds; the combined configured log/evidence budget cannot exceed 32 MiB. The defaults allow four MiB of logs plus up to 6.25 MiB of capture evidence, alongside small runtime information and a sanitized configuration. ZIP entries use fixed safe names and known recording identities.

## Redaction and confinement

The host registers the current access token and loaded/new SMTP credential before use. Exact known values, labeled password/token/credential values, Basic/Bearer authorization strings, URI user information, and HTTP URL queries are redacted before persistence. Reads redact values again, including credentials registered after an entry was written. Configuration bundles mask credential references, usernames, sender/recipient addresses, and sensitive keys; capability errors are sanitized too. Values are sanitized before JSON serialization so downloadable JSON and JSONL remain valid.

The host uses the diagnostics provider for runtime logging. Default console/event-log providers must be removed or explicitly filtered, because installing a redacting provider cannot sanitize a second provider's copy of an exception. Do not introduce log statements containing raw HTTP bodies, headers, private worker option objects, or SMTP authentication data. Redaction supplements those input boundaries; it does not justify collecting secrets in the first place. Diagnostic paths, source/device labels, and technical error details can still be operationally sensitive; review a bundle before sharing it outside the organization.

All diagnostic storage ancestors and evidence entries are checked for symbolic links, junctions, and other reparse points. Traversal, caller-selected paths, and links are rejected. Service and cache ACLs must prevent untrusted concurrent directory replacement: managed checks cannot eliminate a hostile directory-swap race. Reading diagnostics never deletes media or alters a recording package.

## Verification

```powershell
dotnet test tests/DeckCapture.Tests/DeckCapture.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~DiagnosticsTests|FullyQualifiedName~MaintenanceLease"
```

The tests cover disabled defaults and independent persistence, rotation/restart bounds, secret redaction in exceptions and valid JSON downloads, request-query exclusion, fixed IDs, bounded evidence, actual reparse escapes, logging-disk failure isolation, sanitized bundles, and maintenance admission without real devices or network shares. Symbolic-link tests on Windows require Developer Mode or permission to create symbolic links. Use `-p:BuildProjectReferences=false` for a test-only rebuild while an already-built development host is running; a final full build must still validate source changes after that host stops.
