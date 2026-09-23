# OAuth / Public Plugin Architecture

Jarvis uses OAuth as its only public authentication model. ChatGPT never receives or supplies a Jarvis session secret. The local Jarvis client authenticates as a user-owned device and MCP calls route by validated OAuth identity.

## Production identity

- Auth0 issuer: `https://dev-kn4j3jz3qv2cvw05.eu.auth0.com/`
- Resource/audience: `https://jarvis2.kehlet.dev`
- MCP endpoint: `https://jarvis2.kehlet.dev/mcp`
- Native callback: `http://127.0.0.1:43821/callback`

The native client ID is public OAuth metadata and is compiled as the default client configuration, with environment-variable overrides for alternate deployments.

## Architecture

1. MCP initialization and tool discovery are reachable without workspace access so clients can inspect OAuth requirements.
2. Every tool that accesses user data or performs an action resolves identity from the validated bearer token and enforces its required scope.
3. Missing/insufficient authentication returns an MCP OAuth challenge in `_meta["mcp/www_authenticate"]`.
4. The server publishes RFC 9728 protected-resource metadata.
5. JarvisClient authenticates through Authorization Code + PKCE and connects to SignalR with `client:connect`.
6. The server derives the client and MCP caller's user identity from Auth0 `sub`.
7. Device registration contains only non-secret device/protocol metadata.
8. The local Jarvis permission policy remains an independent authorization boundary for mutating/process/git operations.
9. The profile tool returns a deterministic opaque hash of the OAuth subject rather than the raw provider identifier.
10. The public OpenAI domain-verification token can be configured with `JarvisOpenAIAppsChallenge`.

## Scopes

- `workspace:read`
- `workspace:write`
- `process:execute`
- `git:write`
- `client:connect`

## Removed architecture

The following are intentionally gone:

- copied temporary session keys;
- model-visible routing credentials;
- static MCP/API keys;
- the legacy `/agent` HTTP surface;
- the Actions OpenAPI schema;
- custom-GPT-specific instructions and compatibility aliases.

## Plugin package

The portable plugin package lives in `plugin/` and contains:

- `plugin.json`;
- `mcp.json`;
- `skills/project-work/SKILL.md`.

Public review material, tool-annotation justifications, and test cases are in `docs/PluginSubmission.md`.

## External release prerequisites

These are deployment/account tasks rather than code work:

- ensure the five custom Auth0 API scopes are defined and granted appropriately;
- deploy the current server/client artifacts;
- connect ChatGPT in developer mode and exercise the OAuth flow;
- set the OpenAI domain-verification token when the submission portal provides it;
- provide publisher identity, support/privacy/terms URLs, a reviewer account, and a demo recording;
- run the production Scan Tools/review workflow.
