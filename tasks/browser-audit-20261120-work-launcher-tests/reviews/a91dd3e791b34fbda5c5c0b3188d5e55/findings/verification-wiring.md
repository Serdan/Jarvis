# Blocking finding: CI/release verification is not connected to the new Work suite

**Reviewed source:** `ef602c333737bc8eb32bffcda4cfb012d6a5009d` (base `d702352ef705f9991ac1cb14dc15a13cce78aa44`).
**Original requirement:** "[New] dedicated launcher suite runs in the repository's standard node test invocation and is included in CI/release verification."

## Reproduction / evidence
1. `browser-extension/README.md:106-113` now documents `node --test browser-extension/tests/*.test.cjs` and correctly includes the new suite in a *manual* shell-glob invocation.
2. `.jarvis.json` defines the canonical `test` project task as `dotnet scripts/build.cs test`.
3. `scripts/build.cs` `Test(configuration)` invokes only `Client.Tests/Client.Tests.fsproj` and `Server.Tests/Server.Tests.fsproj`, with no Node extension tests.
4. The frozen source has no tracked `.github/workflows` or other discovered automated CI wiring. No actual release verifier executing the Node command was demonstrated.
5. Independent direct `node --test --test-reporter=spec browser-extension/tests/chatgpt-work-launcher.test.cjs` passes **23/23**. This establishes runnable tests, not execution by CI/release verification.

## Impact / scope
The new tests can remain green locally while the repository's canonical verification path ignores them, so a later Work launcher regression will not fail that gate. This is a gap against the **original test-task acceptance criterion**, not a request to change production deployment or implement new CI infrastructure.

## Proportionate correction
Provide an executable, demonstrably used canonical CI/release verification entrypoint that invokes the Work suite (or the project extension Node suite) and record verification evidence. If an explicitly operator-approved manual release verification process is the intended gate, document its invocation/ownership and acceptance evidence instead. The separately owned existing Chat launcher failures (64 in the five-file invocation; legacy suite untouched by this patch) must not be silently attributed to this Work patch; avoid tying a new gate to a known red unrelated suite without separate disposition.
