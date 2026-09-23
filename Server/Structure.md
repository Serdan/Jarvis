# Server Module Structure

- **Auth.fs** — OAuth scope constants, issuer/resource helpers, subject extraction, and scope checks.
- **JarvisOptions.fs** — Auth0 resource-server and OpenAI domain-verification configuration.
- **McpTools.fs** — Public MCP tools, OAuth scope enforcement, tool annotations, profile tool, and typed MCP errors.
- **Program.fs** — ASP.NET host, JWT bearer authentication, protected-resource metadata, MCP endpoint, SignalR endpoint, rate limiting, and OpenAI domain-verification challenge.
- **Services/UserService.fs** — In-memory authenticated user/device connection state.
- **Services/HubService.fs** — Authenticated SignalR hub and device registration.
- **Services/ClientService.fs** — Routes MCP commands to the active local device and tracks responses.
- **Services/ClientResponseTracker.fs** — Correlates server requests with local client responses.
