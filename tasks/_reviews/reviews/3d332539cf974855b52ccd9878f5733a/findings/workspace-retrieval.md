# [P2] Workspace AGENTS.md omission points to an inaccessible read_file path

**Locations:** `Client/Modules/AgentInstructions.fs:120-144,205-225`, `Client/Modules/ProjectPaths.fs:273-287`, `Client/Modules/ProjectBrowser.fs:697-716`.

The omission warning always advises `Use read_file to retrieve the complete file`, including `Scope = "workspace"` and displayed path `AGENTS.md`. But `read_file` always resolves `filePath` relative to the selected *project checkout*, not the enclosing workspace. Thus `read_file(projectName="Jarvis", filePath="AGENTS.md")` reads Jarvis/AGENTS.md, not the workspace root AGENTS.md, while `filePath="../AGENTS.md"` is forbidden.

**Verified:** A real read_file call for `../AGENTS.md` on the feature worktree returned `PermissionDenied: Path cannot escape project root: ../AGENTS.md`.

If the workspace instructions exceed the per-file budget, Jarvis delivers none of them and suggests a retrieval action incapable of obtaining the missing text. That contradicts the required explicit usable retrieval guidance.

**Request:** Provide an actually supported way to fetch complete workspace-root instructions (while preserving path authorization), and make omission notices scope-specific. Add an oversized workspace AGENTS.md integration test validating the suggested retrieval method.
