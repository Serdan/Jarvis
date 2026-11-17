# Disposable empty-server Work continuity fixture

Standalone test-only project. Run: dotnet test Client.EmptyServerRestartFixtures/Client.EmptyServerRestartFixtures.fsproj -c Release

Creates independent original bare Git remotes, each with two clones, for a project and account authority. The test invokes real project Git-CAS Work credential verification and the real in-memory server UserService, then discards the server registry and SQLite diagnostics before reconnecting both clients. Only Jarvis-level AGENTS.md and config.json are recreated in the empty server directory.

Coverage: original Work first-connect and claim across two clones; original UUID replay versus duplicate/stale first-connect and invalid bearer; checkpoint from another clone; STOP revocation, exact pre-STOP receipt replay; original account and project heads; preexisting Task, independent review and summary, Task/Role operator message history and message operation replay; precise legacy Agent/instruction/role/notification blob-OID preservation or fully empty Agent history; old transport rejection/new registration; absent server activity proof preventing Manager AfterAgentIdle launch.

Authority boundary: Project Git CAS is the only durable Work evidence; the server registry/SQLite are never authorization sources. Existing default-off client fixture methods are used directly: executeInertFixtureAt and executeCredentialFixtureAt. Work-native source ownership, migration, Scheduler, production browser dispatch and live service restart remain untouched.

Production NO-GO: a real provider-private execution bootstrap, authenticated provider-execution binding, and authenticated credential-bound Work first-connect relay are missing. The typed HostBridgeUnavailable result is asserted. This is a disposable regression fixture, not evidence that a real provider can launch Work or that production cutover is permitted.
