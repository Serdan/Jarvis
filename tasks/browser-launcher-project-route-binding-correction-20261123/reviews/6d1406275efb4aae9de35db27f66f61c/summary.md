# Independent review — ChangesRequested

Frozen head `90acadead16c8e0708ad4da323bccca538e2c907`, source-only Stage B; current task spec `f05690d43b43cc775ce9c778447d4cdb5364b111`. Claimed request `6d1406275efb4aae9de35db27f66f61c`.

## Evidence and provenance

- Independently checked clean exact checkout `reviewer4-route-90acadead`, HEAD above. Parent history shows base `d63c49dacda93e246c3b38fe3854ea7a0b2f2135` and exact accepted `118db3aff379a47afc1243821d0976c84701fadd` and `415ef34cf6717be2d0aad00c3dc5b3a224ed6828` via merges `c67a7eb6791cb351b92a9ad777730ee05b9b5f2c` and `a5806be573a0596916be2738ca4d2dbbec60134e`. Inspected remerge conflict resolutions (model and observed labels), scoped five-file correction and diff --check (clean).
- Independent Node run of chatgpt-launcher, native-private-host-gate, bridge and popup suites exited 0 (job f79e3d336d91426198585deefb150e83; output buffer truncated, so no independent numeric count asserted).
- Independent .NET scoped BrowserBridgeTests/ReviewWorkflowTests build succeeded; test run began but did not return results before cancellation after decisive independent failing fixture (job a3591055721c4dfe9dd5acac6ef2651b, verified Canceled). No independent .NET pass/fail assertion. Author reported 223/223 Node and 67/67 Release BrowserBridge plus existing attribution test; these are author-reported counts.
- Source inspection supports removal of sidebar fallback, one-send behavior, typed route-unknown/null URL retention exception, and preservation of generic/success cleanup under the specified flag. No provider or live execution evidence; no source edits, deployment, activation or integration.

## Blocking finding

[stale-turn-across-route-transition.md](findings/stale-turn-across-route-transition.md): independent exact-harness negative-space test failed (exit 1). Matching user bubble at t+75, unrelated same-project durable route B at t+300, B's mismatched DOM at t+375. The launcher reports submitted URL B before B's DOM replacement; one Send and zero launcher navigations. The same-poll route/text check does not bind a previously observed bubble to the new route. This violates the existing causal association/unknown-if-unbound requirement, not merely a preferred solution. Existing regression synchronizes route and changed text in one event and misses this ordering.

ChangesRequested for this frozen SHA. Independent bounded adjudication of the defect/remedy is needed; do not turn a candidate remedy into new scope automatically. If no demonstrable route association exists, preserve acknowledged uncertainty with null URL and retention. No claim of actual live incidence or provider behavior.