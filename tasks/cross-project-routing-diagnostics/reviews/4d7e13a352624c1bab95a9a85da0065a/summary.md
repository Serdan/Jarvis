# Review decision — Accept

Exact source snapshot `ce72811a1138e85e3b13865102b0e85678c96af3`, incremental base `3d409899cd506a943d24991fd75309ec9034ac67`.

The former catch-all error incorrectly advised protocol upgrade when all eligible clients had failed LedgerHealth probes. The change retains selected-first candidate ordering, capability/connected/protocol filters, destination health verification, and the critical no-fallback-after-write policy. A rejected candidate now reports a specific eligibility reason; an eligible candidate whose health probe failed reports a sanitized error category. At most eight per-device details are included. Arbitrary probe error strings are not exposed. Three new server tests cover mixed compatibility, sanitized probe errors, and no-device messaging. No blocking finding.

Test evidence: Server.Tests direct test command exited 0. Canonical full suite reported 344 passed of 345 client tests; sole failure was previously intermittent `startJob strips sensitive inherited environment` 5-second timeout. `git diff --check` clean.

Decision: Accept. Suitable for normal merge to main. This does not fix any underlying binding or routing failure, only improves its diagnosis. Production requires separate deployment approval.