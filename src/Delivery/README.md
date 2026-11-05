# Delivery component

`FileDeliveryEngine` implements `IFileDelivery` from Domain. Register exactly one engine in the supervisor so transfers share an aggregate token bucket. The caller owns admission, concurrency, persisted job state, retries, holds, and expiring overrides. Only finalized immutable capture packages with validated artifact snapshots may be submitted.

See [delivery operations](../../docs/operations/DELIVERY.md) for publication, recovery, scheduling, trust boundaries, and test commands. This project uses only the .NET base class library and Domain.
