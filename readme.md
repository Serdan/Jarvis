# JARVIS

## Overview
Jarvis is a collaborative agent designed for managing project files that need to be stored externally from the agent. It provides a suite of tools and functionalities to enhance project file management and accessibility.

## Features

### Project Directory Listing
- **List Project Directories**: Jarvis can list all directories within a specified project, providing a clear view of the project's structure.

### Enhanced File Metadata
- **File Metadata Retrieval**: Jarvis now includes additional metadata for files in directory listings, such as file size, creation date, and modification date. This feature enhances the visibility and management of project files.

### Secure File Handling
- **Secure Path Management**: Jarvis ensures secure handling of file paths, preventing leakage of sensitive system information.

### Project File Management
- **Open and Edit Files**: Users can open and edit project files directly through Jarvis, streamlining the project management process.

### Real-Time Collaboration
- **Collaborative Environment**: Jarvis supports a collaborative environment, allowing multiple users to interact with project files simultaneously.

## Usage
[Provide a brief guide or examples on how to use the key features of Jarvis]

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
  --server https://jarvis.kehlet.dev/client
```

Publish multiple stamped clients:

```bash
dotnet scripts/build.cs publish-clients \
  --server https://jarvis.kehlet.dev/client \
  --rid linux-x64 \
  --rid win-x64 \
  --rid osx-arm64
```

Build outputs are written to `artifacts/client/<rid>/` by default. The client executable is named `JarvisClient` (`JarvisClient.exe` on Windows).

Publish a self-contained single-file server:

```bash
dotnet scripts/build.cs publish-server \
  --rid linux-x64
```

Server outputs are written to `artifacts/server/<rid>/` by default. The server executable is named `JarvisServer` (`JarvisServer.exe` on Windows).

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
