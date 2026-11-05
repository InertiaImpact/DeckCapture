# Delivery operations

The supervisor submits a finalized immutable package and its artifact sizes and SHA-256 hashes to one shared `FileDeliveryEngine`. It remains responsible for queue admission, maximum concurrency, persisted attempts and progress, holds, retry delays, and the expiry of global or individual overrides. A browser connection has no role in delivery lifetime.

## Package publication

The final directory is `<DestinationRoot>/<RelativeDirectory>/<JobId>`. JobId is a stable, safe single path segment; relative folders may contain multiple safe segments. The supervisor displays the actual resulting path. Artifacts preserve their relative layout. Source files are never deleted or altered by delivery.

Before copying each file, the engine reads the source and checks both its exact size and SHA-256 against the immutable artifact snapshot. Its source handle disallows write sharing on Windows until that file finishes copying. The engine copies into the same destination root at `.deckcapture-staging/<SHA256(JobId)>`. A `.DELIVERY.json` ownership record binds this private package to its source and artifact snapshot. A separate exclusively opened lock file prevents overlapping delivery attempts against the same job, including attempts from another process.

A retained destination prefix is read and compared byte-for-byte to the source before appending. File length alone never authorizes resume. A longer or corrupt partial fails safely and is preserved for operator investigation; the engine does not silently discard or overwrite it. After copying, every destination artifact is opened again and hashed completely. Only after all hashes match is `COMPLETE.json` written, containing the stable JobId and complete artifact list. Both metadata records stay with the published package.

The final publication is one directory rename within the destination root. An existing path is never overwritten. If the process loses its response after a successful rename, a repeated request can reconcile the already-published directory only when its completion manifest matches and all source and destination hashes pass again. A completion marker alone is insufficient evidence. An existing unrelated package, an unexpected file, or mismatched ownership metadata causes an error without changing those bytes.

`CheckpointDirectory` is retained in the Domain request for supervisor coordination; the delivery engine needs no separate checkpoint database. The durable destination staging directory is the resume checkpoint. Do not change JobId or source/artifact identity while retrying a package.

## Cancellation and recovery

Cancellation is checked during hashing, prefix checks, copy reads/writes, bandwidth waits, and before publication. Completed copy buffers are flushed; partially written files remain available for validated resume. Cancellation before verification does not create a completion marker. Cancellation after all verification may leave a fully verified staging package with a completion marker; retry validates it again before publication. Markers are written with create-new semantics and flushed to storage. A torn metadata write causes a safe identity failure requiring review, never a false delivered result.

A failed rename retains staging. A server or volume that cannot provide an atomic same-root directory rename must not be accepted as a production destination. SMB, service identity access, disconnect/reconnect behavior, storage durability, and power-loss recovery still require acceptance testing on the actual archive server. Software tests use local temporary directories and cannot establish those server guarantees.

Nothing automatically cleans retained local recordings, staging packages, or small lock files. Before manually removing or moving a failed staging package, stop its delivery attempt and investigate the recorded error. Do not delete the local source of an undelivered package. A previously published package whose hash later differs fails reconciliation and must be investigated rather than automatically replaced.

## Bandwidth and scheduling

The live rates callback supplies finite positive per-transfer and aggregate MiB/s limits. One MiB is 1,048,576 bytes. Each destination write, retained-prefix read, and final verification read consumes both a private token bucket and the shared aggregate bucket together. Source validation reads are local-cache reads and do not consume destination bandwidth. Buckets use monotonic elapsed time and allow at most one 64-KiB buffer of burst credit. Queued waiters reserve no tokens, and cancellation does not strand another transfer's tokens. Rates are re-read at least every 50 ms while waiting, and before every buffer; a new lower rate can retain bounded existing burst credit. All concurrent callbacks must read the same current aggregate setting.

The supervisor uses `TransferWindowPolicy.IsWindowOpen(settings, timeZone, utcNow)` for admission and chooses `PerTransferMiBps` inside the window or positive `OutsideWindowMiBps` outside it for an already-running transfer. Overrides permit admission only; they do not change the rate callback or bypass other safeguards. Reconnected attempts require admission again.

Windows use explicit operating-system time zone data and strict `HH:mm` boundaries. Start is inclusive and end is exclusive. Overnight windows belong to their start day. Equal start/end times are rejected; configure distinct finite windows rather than an implicit full-day interval. Multiple windows form a union. An empty enabled schedule admits nothing; a disabled schedule is always open. During a fall-back overlap, a start uses its earlier UTC occurrence and an end its later occurrence. During a spring-forward gap, a missing boundary advances to the first valid local minute. Persist instants in UTC; do not persist an ambiguous local date/time as a retry or override expiry.

## Path and trust boundary

Source and destination roots must be absolute and cannot overlap in either direction. Relative paths reject rooted paths, traversal, empty segments, alternate data streams, reserved Windows device names, trailing dots/spaces, and invalid characters. Artifact paths cannot collide case-insensitively or turn a file into another artifact's parent. `COMPLETE.json` and `.DELIVERY.json` are reserved. The `.deckcapture-staging` destination folder is private.

Existing ancestors and package entries are checked for symbolic links, junctions, and other reparse points before use, and directories are rechecked after creation and before publication. Inventory walks do not follow reparse points. Configure destination and cache ACLs so untrusted accounts cannot replace directories while the service is operating: managed path checks cannot eliminate a hostile concurrent directory-swap race, and hashes and markers do not authenticate a malicious storage administrator. Source immutability and trusted configured roots remain caller and deployment requirements.

## Verification

Run from the solution directory:

```powershell
dotnet build src/Delivery/DeckCapture.Delivery.csproj --no-restore -m:1 -nr:false
dotnet test tests/DeckCapture.Tests/DeckCapture.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~DeliveryTests|FullyQualifiedName~ScheduleTests"
```

Regression tests cover byte-preserving cancellation and resume, same-length corrupt prefixes, corrupt source and destination rejection, publication reconciliation and collisions, path confinement and actual symbolic-link escapes, invalid rates, aggregate limits across copying and verification, live rate changes, schedule union/overnight boundaries, and both DST transition rules. Tests create disposable synthetic files under a unique temporary root and make no network transfers. On Windows, the symbolic-link regression tests require Developer Mode or permission to create symbolic links; this host has Developer Mode enabled. The single-node build flags avoid excessive MSBuild worker creation on high-core-count workstations.
