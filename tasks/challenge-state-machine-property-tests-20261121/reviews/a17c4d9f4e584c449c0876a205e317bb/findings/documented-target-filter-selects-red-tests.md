# P2 — Documented targeted Release command has a false green result

**Frozen source:** `cd895d493b419e127ededea15f814ab53e484407`.

The committed `docs/testing/challenge-state-machine-property-tests-20261121.md` reports:
`dotnet test Client.Tests/Client.Tests.fsproj -c Release --filter FullyQualifiedName~ChallengeStateMachinePropertyTests --no-restore --verbosity minimal` → exit 0, **2/2** pass.

**Independent execution** of that exact filter at frozen HEAD with .NET SDK 11.0.100 Release, `--no-restore`, returned **exit 1; 6 passed / 3 failed / 9 total**. This is because the F# top-level module `Client.Tests.ChallengeStateMachinePropertyTests` prefixes the fully qualified names of *all* contained NUnit fixtures, including `ChallengeReverseIndexFaultTests`; the documented substring filter does not isolate the two green model tests. The three failing cases were exactly fault seeds 9000–9002, not build or fixture errors.

**Required correction.** Document an actual tested filter that selects the two intended methods (e.g. by their exact test names), and list the full namespace-prefix filter's expected red result separately. Provide source-bound command/exit/test counts so reviewers cannot mistake a deliberately red suite for a green gate.