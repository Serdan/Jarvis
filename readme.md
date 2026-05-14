# JARVIS

## Overview

Jarvis is a collaborative agent for working with project files that live outside the agent runtime. A local Jarvis client connects to the Jarvis server, exposes selected project directories, and lets an agent list, search, read, edit, test, and commit project changes with user-controlled permissions.

## Features

### Project Directory Listing
- **List Project Directories**: Jarvis can list directories within a configured project and show a clear view of the project structure.

### Enhanced File Metadata
- **File Metadata Retrieval**: Directory listings include file size, creation date, and modification date.

### Secure File Handling
- **Secure Path Management**: Jarvis resolves project-relative paths and prevents access outside the selected workspace/project root.

### Project File Management
- **Open and Edit Files**: Users can let an agent read, write, patch, and inspect project files through the local client.

### Command Execution and Git
- **Bounded Local Commands**: Jarvis can run approved commands such as tests and builds.
- **Git Operations**: Jarvis can read status/diffs and create approved local commits.

### Real-Time Collaboration
- **SignalR Client Connection**: The local client connects to the server and receives commands in real time.
- **Permission Prompting**: Mutating, process, and version-control commands require local approval.

## Usage

Download the client for your operating system, run it, choose the workspace directory that contains your projects, then provide the displayed key to the agent.

Production client downloads:

```text
https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
https://jarvis2.kehlet.dev/downloads/JarvisClient-osx-arm64
https://jarvis2.kehlet.dev/downloads/JarvisClient-win-x64.exe
https://jarvis2.kehlet.dev/downloads/SHA256SUMS
```

Linux example:

```bash
curl -fsSLO https://jarvis2.kehlet.dev/downloads/JarvisClient-linux-x64
chmod +x JarvisClient-linux-x64
./JarvisClient-linux-x64 --path ~/Projects
```

## Build Scripts

Jarvis uses a .NET 10 file-based C# build app with `System.CommandLine`:

```bash
dotnet scripts/build.cs --help
```

Common commands:

```bash
dotnet scripts/build.cs test
dotnet scripts/build.cs compile
```

Publish one self-contained single-file client with a server URL embedded at build time:

```bash
dotnet scripts/build.cs publish-client \
  --rid linux-x64 \
  --server https://jarvis2.kehlet.dev/client
```

Publish multiple stamped clients:

```bash
dotnet scripts/build.cs publish-clients \
  --server https://jarvis2.kehlet.dev/client \
  --rid linux-x64 \
  --rid win-x64 \
  --rid osx-arm64
```

Build outputs are written to `artifacts/client/<rid>/` by default. The client executable is named `JarvisClient` (`JarvisClient.exe` on Windows).

Publish nginx-ready client downloads:

```bash
dotnet scripts/publish-client-downloads.cs
```

This writes stable download names and `SHA256SUMS` to `artifacts/downloads/`. See `docs/ClientDownloads.md` for the Ubuntu/nginx deployment steps.

Publish a self-contained single-file server:

```bash
dotnet scripts/build.cs publish-server \
  --rid linux-x64
```

Server outputs are written to `artifacts/server/<rid>/` by default. The server executable is named `JarvisServer` (`JarvisServer.exe` on Windows).

Publish the Jarvis 2 linux server and client:

```bash
dotnet scripts/build.cs publish-jarvis2-linux
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
