# Explicit UUIDv4 Work execution credentials (default-off)

## Original protocol and compatibility

This change builds on the accepted WQ02 original reservation credential verifier,
not on a second identity ledger. Each **new** reservation obtains a distinct,
cryptographically random .NET 11 UUIDv4 bearer (Guid.NewGuid().ToString("D"))
in IssuedWorkCredential.Issue. The public launchId remains a nonsecret
index into the original Git-backed reservation. The immutable original WQ02
same-CAS WorkCredentialVerifier (schema 1) stores **only SHA-256 of the
canonical text**, plus original project/launch/account scope. No credential
preimage enters project Git, receipts, status or permission approval arguments.

A previously admitted 43-character Base64URL credential continues to verify
against its existing persisted hash. Only new issuance changes to UUIDv4;
no incoming label, public launch ID, role, Session UUID or credential format
alone is authentication. A guessed UUIDv4 never grants access:
the same original verifier digest must match with fixed-time comparison.

## Execution/tool protocol

The Work execution model is handed the plaintext UUIDv4 **once** as part of
its launch bootstrap. It then supplies that exact string as the explicit
credential argument to each Work execution tool invocation:
ConnectWork, ClaimNextWork, CheckpointWork and CompleteWork as of
this change. The typed WorkQueueWriteCommand.Credential remains transient
during the MCP-to-client relay. Its preflight checks the original Git verifier
and stored launch state at the current verified head, never an asserted
agentId/role. No bridge/client is expected to secretly add the argument.

Existing Work verifier actions preserve the original first-registration
idempotence, claim generation/STOP, completion and exact operation replay
semantics. Missing/malformed/wrong/foreign credentials fail closed. The
original offline adapter still demonstrates the two-clone fake dispatch and
first-connection contract, with an explicitly provided bearer. Its old
NativePrivateBootstrap is a disposable compatibility seam, **not** proof of
an automatic provider channel or provider-authenticated session. It must not
be treated as a production dependency for the explicit model-argument design.

## Disabled production boundary

The public Work write tools still return RejectedMissingBinding even after
successful original-Git credential authentication. They do not register,
claim, checkpoint or finish production Work yet: WQ03 original dispatch/source
and WQ04 same-CAS host evidence must first be wired independently. No
implicit Agent fallback, new permission system, new server-side credential
database, live provider send, Scheduler, deployment or cutover is introduced.
All historical Agent API compatibility remains separate. Operator-approved
manual grant issuance is a dependent task, **not implemented here**.

Offline evidence: WorkQueueCredentialTests covers new UUIDv4 uniqueness,
canonical version/variant, SHA-256, 2-clone original Git verifier,
model-argument denial/valid-but-default-off behavior, existing Base64URL
verifier compatibility, STOP and replay. Existing native adapter/host,
permission and fence tests remain regression coverage. Run only local
.NET 11 Release tests; hosted Actions are not required.
