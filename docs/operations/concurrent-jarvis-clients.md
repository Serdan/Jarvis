# Concurrent Jarvis clients: identity, routing, and shared state

This document describes the `jarvis2:` client route format introduced for concurrently running JarvisClient processes. It is an application-level routing ID, **not** an authorization token. The authenticated SignalR user and transport remain the authority for registration, and command authorization remains unchanged.

## Starting and selecting clients

Run the normal `JarvisClient` program in two terminals. Each process now generates a fresh **instance UUID** at start and retains that UUID through its SignalR reconnects; its **machine UUID** remains in the historical `Jarvis/device-id` application-data file. Its **installation UUID** remains in the separate local-application-data `Jarvis/installation-id` file. Multiple processes using the same application-data directory share both persistent IDs but must have different instance UUIDs.

`list_devices` lists one entry per running process, exposing `deviceId` (a versioned `jarvis2:<machineUUID>:<installationUUID>:<instanceUUID>` route), `deviceName`, `machineId`, `installationId`, and `instanceId`. Use the **entire deviceId** with `select_device` to route this conversation, or with an explicit per-command `deviceId` override. A human-readable machine name alone is not unique. On an old client the deviceId remains an opaque stable string; that form is still supported.

One process disconnecting does not invalidate another process's route. An explicitly requested disconnected process fails rather than silently routing to another one. Defaults and conversation selections retain their prior behavior, except that a disconnected legacy UUID route is migrated to an active versioned route for that same machine. This does **not** retarget an explicitly supplied device ID.

If an optional local browser bridge is configured, only one process can bind a particular `--browser-port`. A process that cannot bind the shared port remains a working Jarvis client without a browser bridge. Choose different `--browser-port` values for simultaneous browser-enabled instances and pair each extension to the intended port. Scheduled browser/CLI executions use an exclusive scheduler ownership lease; a second process must not execute the same persisted schedule.

## Machine IDs, configuration copies, and collisions

The original `device-id` is preserved to avoid silently changing the installation's established machine identity. Identity creation and validation are serialized across cooperating processes with an OS-managed exclusive file lock, and the newly generated ID is published as a complete file. An empty, corrupt, linked, or unreadable identity file blocks startup rather than being overwritten. The local pairing key is similarly protected.

The client sends machine, installation, and instance UUIDs inside its versioned route ID. If the server sees two different installation UUIDs claiming the same machine UUID for the *same authenticated user*, it rejects the second registration **before changing any existing route**. This identifies an ordinary copied roaming configuration while both installations are known to the server. Existing legacy opaque client IDs remain valid.

If a clone reports a collision, **stop all JarvisClient processes on the cloned installation**, first verify which actual machine should retain the identity, then back up and deliberately remove or rename only that clone's `device-id` file. On the next launch the clone generates a new UUID. Confirm `list_devices` before selecting it. Never copy identity files or OAuth credentials to another machine as a way to provision a client.

**Evidence boundary:** Two fully cloned installations that carry identical machine *and* local-installation files, or one that connects after the in-memory registry has restarted/forgotten its peer, cannot be distinguished using those files alone. An ephemeral server has no historical global uniqueness oracle. No unsupported assertion of perfect clone detection or an automatic destructive repair is made. A future stronger design would need independently trusted host provenance and an authorized continuity protocol.

## Concurrent state protections

- Client configuration saves acquire an inter-process file lock, re-check the loaded revision, and use atomic replacement; stale edits report a conflict rather than overwriting changes.
- OAuth refresh-token read/rotation/persistence is serialized by an inter-process lock. The second process reloads the latest stored token after acquiring the lock; the same lock prevents simultaneous initial browser callback listeners on the configured OAuth redirect port.
- Browser pairing-key creation uses its own atomic, exclusive creation lock. Browser sessions and running jobs stay process-local.
- Agent activity snapshots merge disjoint or newer last-seen timestamps under a cross-process lock instead of overwriting another client's observations. Corrupt existing snapshots block replacement.
- The scheduled-execution store already has an exclusive lifetime owner lease. Git ledger commands still use their existing expected-head/CAS, exact operation IDs, and source/worktree scope; routing IDs do not relax those invariants.
- The server's device/session registry remains in memory, reconstructible when clients reconnect after restart. This change introduces no durable server routing database, no background scheduler cutover, and no deployment.

**Operational scope:** Local running-job and browser registration handles belong to the process that started them. A job ID from one instance cannot be expected to resolve or cancel in a second instance; select the owning `deviceId`. A missing result from a different process is not proof that the effect never occurred.

## Verification

Release tests cover simultaneous first-start identity and browser-key races, cloned-machine collision rejection, independent per-instance registration and disconnect, explicit route separation, legacy selection migration, and re-registration on an empty server. Manual acceptance should additionally launch two real clients against the same workspace, exercise both directed MCP commands and reconnects, and restart only the server under operator authorization. Do **not** deploy this branch solely because unit tests pass.
