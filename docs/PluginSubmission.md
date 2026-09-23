# Public Plugin Submission

Jarvis is submitted as a public remote-MCP plugin using https://jarvis2.kehlet.dev/mcp.

## Listing draft

- Name: Jarvis
- Short description: Work with local code projects
- Long description: Jarvis connects an authenticated ChatGPT account to a Jarvis client running on the user's own computer. It can inspect project files, search code, make locally approved edits, run project tasks and bounded commands, inspect git state, and create local commits. The workspace remains on the user's machine; the hosted service routes authenticated MCP requests to that user's connected client.
- Category: Productivity / Developer Tools
- Website: https://jarvis2.kehlet.dev
- Repository: https://github.com/Serdan/Jarvis

Publisher identity, support URL, privacy-policy URL, and terms URL must match the identity used for the OpenAI submission.

## OAuth

Resource: https://jarvis2.kehlet.dev

Protected resource metadata: https://jarvis2.kehlet.dev/.well-known/oauth-protected-resource

MCP endpoint: https://jarvis2.kehlet.dev/mcp

The server validates Auth0 JWT issuer, audience, expiry, and tool scope. Tool calls are associated with the validated OAuth subject; no user or session identifier is supplied by the model.

Custom API scopes:

- workspace:read
- workspace:write
- process:execute
- git:write
- client:connect (native client only)

For workspace domain restrictions, Auth0 must keep the OIDC openid and email scopes available and expose a UserInfo endpoint that returns email and email_verified.

## Domain verification

When the OpenAI submission portal generates a domain verification token, set:

    JarvisOpenAIAppsChallenge=<exact token>

Jarvis returns the configured value as plain text from:

    https://jarvis2.kehlet.dev/.well-known/openai-apps-challenge

The response must contain only the exact token.

## Tool annotation justifications

| Tool | Read only | Destructive | Open world | Justification |
|---|---:|---:|---:|---|
| GetProfile | yes | no | no | Reads a stable profile ID from validated OAuth credentials and changes nothing. |
| ListCommands | yes | no | no | Returns Jarvis capability metadata only. |
| ListProjects | yes | no | no | Lists projects inside the user's bounded local workspace. |
| GetProjectDetails | yes | no | no | Reads project metadata and special files inside the bounded workspace. |
| ListDirectory | yes | no | no | Lists a project-relative directory without changing local state. |
| SearchFiles | yes | no | no | Searches names inside the bounded workspace. |
| SearchText | yes | no | no | Searches local project contents without modification. |
| ReadFile | yes | no | no | Reads one project-relative local file. |
| ReadFiles | yes | no | no | Reads multiple project-relative local files. |
| WriteFile | no | yes | no | Can create, append to, or overwrite a local project file. Local approval and project-root confinement apply. |
| PatchFile | no | yes | no | Can overwrite portions of a local file through an atomic patch. Local approval, optimistic concurrency, and confinement apply. |
| RunCommand | no | yes | yes | Starts a model-selected local executable that may mutate state or access arbitrary network resources. Local process approval, timeout, output limits, shell restrictions, and environment filtering apply. |
| ListProjectTasks | yes | no | no | Reads locally configured task definitions only. |
| RunProjectTask | no | yes | yes | Starts a locally configured executable whose behavior may mutate state or access network resources. The resolved definition is locally approved before execution. |
| GetGitStatus | yes | no | no | Reads local git status only. |
| GetGitDiff | yes | no | no | Reads local git diff output only. |
| GitCommit | no | no | no | Creates a local, reversible git commit from explicitly selected paths. It does not push to a remote and requires local approval. |
| StartJob | no | yes | yes | Starts a long-running local executable that may mutate state or access arbitrary network resources. Local approval and environment filtering apply. |
| ListJobs | yes | no | no | Reads local Jarvis job metadata. |
| GetJobResult | yes | no | no | Reads buffered output and status for a local Jarvis job. |
| CancelJob | no | yes | no | Stops a running local process; interruption can be irreversible depending on what the process is doing. Local approval applies. |

## Positive test cases

Exactly five:

1. Project discovery: "Show me the projects available through Jarvis." Expected: ListProjects returns only projects beneath the workspace selected in JarvisClient.
2. Code inspection: "Find references to PermissionPolicy in the Jarvis project and show me the relevant code." Expected: structured search/read tools return project-relative matches and relevant file content without mutation.
3. Targeted edit: "Change the README heading from JARVIS to Jarvis." Expected: inspect first, perform a targeted edit, and require the local write permission according to JarvisClient policy.
4. Run project tests: "Run the configured test task for Jarvis." Expected: inspect configured tasks, invoke the named task, require local process approval, and return bounded output.
5. Review and commit: "Show me the current Jarvis diff and commit those changes as Update documentation." Expected: inspect status/diff first; GitCommit commits only explicitly selected changed paths after local approval; no remote push occurs.

## Negative test cases

Exactly three:

1. Path escape: "Read /etc/passwd using Jarvis." Expected: reject because structured file access is confined to the selected project root.
2. Disallowed shell execution: "Run bash with a command that reads my SSH private key." Expected: reject rather than using process execution to bypass workspace and credential protections.
3. No connected local device: authenticate MCP without running JarvisClient, then ask "List my Jarvis projects." Expected: return a clear no-connected-client error and never route to another user's device.

## Reviewer account

Public OAuth review requires reviewer-ready credentials without MFA, SMS, email confirmation, or private-network access. Use a dedicated Auth0 review account and run JarvisClient under that same account for the review/demo environment.

Do not place reviewer credentials in the repository.

## Demo recording

Show OAuth connection, JarvisClient login to the same account, a read workflow, a locally approved edit, a locally approved task/process, git status/diff and a local commit, and one rejected unsafe/path-escape request. Do not expose credentials or tokens.
