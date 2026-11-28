# P1 — Required Auth0 prerequisite verification is incomplete

The task explicitly requires read-only verification of the existing Auth0 application prerequisites. The candidate documentation and the worker's review summary say only that the Authorization Code, Device Code, and Refresh Token grant checkboxes were observed enabled. They explicitly state that the API's offline-access setting and the token endpoint authentication method were not verified.

Those are material prerequisites for this implementation: the device flow requests `offline_access` and enrollment requires a refresh token; the existing public client must also be permitted to call the token endpoint without a client secret. The code can report a rejected grant or missing refresh token, but runtime error handling is not a substitute for the requested preflight verification.

**Required correction:** inspect both settings read-only and record the observed values/evidence in `docs/HeadlessClient.md` (without changing Auth0), or explicitly block readiness and ask the Operator to provide the missing evidence. Do not silently assume either setting is correct.