# P2 — Existing browser login path lacks an end-to-end regression test

The candidate refactors the shared login/refresh path through `loginAt`, and changes `refresh`/token posting to use an injected client. The new `browserAuthorizationStillUsesPkceAndOfflineScope` test checks only the generated authorization URI. The pre-existing tests cover helper behavior (`presentAuthorization` and `callbackIsValid`), but none invokes the default `OAuth.login` browser path through callback handling, authorization-code exchange, and refresh-token persistence/fallback.

That leaves the explicit compatibility requirement—preserve and test the old browser flow—without an end-to-end fake-endpoint regression test. The focused 15/15 result does not close this gap because those tests never exercise the refactored browser login composition.

**Required correction:** add an isolated fake-endpoint test (or equivalent injected seam) that exercises the default/browser mode through the loopback callback and token exchange, and verifies the existing PKCE/scope and refresh behavior remain intact. Do not use live Auth0 credentials.