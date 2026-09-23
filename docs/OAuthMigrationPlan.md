# OAuth migration

Jarvis will use OAuth as its only public authentication model. ChatGPT must never receive or supply a Jarvis session secret. The local Jarvis client will authenticate as a user-owned device and MCP calls will route by authenticated identity.

## Work

1. Upgrade ModelContextProtocol.AspNetCore to 2.2.0.
2. Replace static MCP authentication with Auth0 JWT validation.
3. Publish protected-resource metadata and authorization challenges.
4. Add per-tool OAuth scopes and MCP safety annotations.
5. Remove the session key from MCP tools.
6. Authenticate the SignalR client and register authenticated devices.
7. Route commands by authenticated user/device identity.
8. Add native authorization-code plus PKCE login to JarvisClient.
9. Delete the legacy API-key, Actions, and custom-GPT surfaces.
10. Add source-controlled plugin instructions/metadata and update tests/docs/deployment.

## Scopes

- `workspace:read`
- `workspace:write`
- `process:execute`
- `git:write`
- `client:connect`

No backward compatibility is required.
