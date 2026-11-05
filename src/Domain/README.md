# Domain contracts

`Models.cs` is shared by supervisor, capture worker, delivery and browser JSON. Read the architecture contracts before changing it. Nullable reference types do not validate untrusted JSON: explicit null entries, nonfinite values, unsupported enums and unsafe paths still require boundary validation.

Persisted JSON currently uses schema version 1. Adding an optional field with a safe default is compatible; renaming fields/enums or changing their meaning requires an explicit migration and restart/recovery tests. Recordings snapshot their channel, capture profile, metadata and destination. Delivery reads an immutable artifact manifest rather than enumerating whatever happens to exist in a directory.

`ChannelConfiguration.FormatCode` defaults to `auto` for new or omitted values. Preserve explicit manual codes during load, export and snapshot round trips; switching existing channels to Auto is an operator configuration change. Auto requests initial detection from the input adapter and does not activate a card profile or guarantee detection without an incoming signal.

Keep external process, HTTP, SQLite and filesystem implementations out of this assembly. The interfaces define ownership and cancellation; operational details belong in the implementation component.

`AppConfiguration.InputWiring` is an optional, empty-by-default list of operator assertions about physical cards and connectors. It is compatible with schema version 1 and travels through export/import and immutable job configuration snapshots. It must never rewrite `ChannelConfiguration.DeviceName` or masquerade as SDK evidence. Supervisor validation bounds its cards, connectors and exact endpoint bindings; capture discovery applies it only as display metadata.

RecordingJob.QcProgress is a nullable schema-compatible progress observation. Older jobs deserialize with null. Stages and counters describe background work only; job state and sealed artifact manifest remain authoritative. Progress persistence does not change operator command revision or physical input ownership.
