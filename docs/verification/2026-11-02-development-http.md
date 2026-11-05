# Explicit development HTTP verification

All 216 application tests passed with real media integration enabled. The new policy cases verify strict HTTPS/token defaults, explicit development HTTP admission, malformed-URL rejection and loopback/DNS/IPv6/wildcard handling. Live loopback HTTP tests verified diagnostics work without a token in the explicit mode and still reject unauthenticated reads when a token is configured. Same-origin and idempotency protections remain active. No development LAN listener or firewall rule was enabled on the development workstation during these tests.
