# Jarvis

Jarvis connects MCP-capable assistants to projects on a user's own computer. An authenticated server routes requests to a local client, which accesses selected workspaces under the user's permissions. Projects do not need to be copied into a hosted development environment.

## Capabilities

- Discover, search, read, and edit project files.
- Run commands and jobs, inspect Git repositories, and manage worktrees.
- Coordinate tasks, agents, assignments, reviews, and integrations with durable state.
- Provide project instructions and reusable skills.
- Optionally automate authorized browser tabs and scheduled actions.

## Architecture

```text
MCP client → Jarvis server → local JarvisClient → selected workspace
```

The server authenticates and routes requests. The local client enforces workspace boundaries and execution permissions. Project instructions guide behavior but do not grant additional authority.

## Getting started

Run JarvisClient with a workspace, sign in, and connect an MCP-capable assistant to the Jarvis server using the same account.

## Documentation

- [Agent coordination](docs/AgentCommandSurface.md)
- [Project instructions](docs/AgentInstructions.md)
- [Browser bridge](browser-extension/README.md)
- [Privacy and data handling](docs/privacy.md)
- [Telos product specification experiment](docs/product-spec/README.md)

## License

[MIT](LICENSE).
