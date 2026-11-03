# Jarvis Skills

Jarvis skills are project-local or workspace-global procedural instructions that an agent can discover, read, and create through the MCP surface. Skills do not execute code, grant permissions, or bypass the normal Jarvis trust model.

## Layout

Store project-local skills under:

```text
<project>/.agents/skills/<skill-name>/SKILL.md
```

Global skills shared by every project under a client's selected workspace live at:

```text
<workspace>/.agents/skills/<skill-name>/SKILL.md
```

Global skills belong to the current local workspace, not to every device or unrelated workspace.

For compatibility, `skill.md` is also accepted when `SKILL.md` is absent. `CreateSkill` always writes the canonical uppercase `SKILL.md` path.

Example:

```text
Wayfold/
  .jarvis/
    skills/
      add-opcode/
        SKILL.md
      release/
        SKILL.md
```

A skill is ordinary Markdown. The directory name is its skill name. `ListSkills` prefers the `description:` entry in standard YAML frontmatter (`---` delimiters), falling back to the first non-empty, non-heading body line for older files.

```markdown
# Add opcode

Implement an Infinity Engine opcode using the existing typed decoding and runtime conventions.

1. Inspect the opcode definition.
2. Add typed decoding.
3. Add runtime behavior separately.
4. Add focused tests.
5. Run the canonical test workflow.
```

## MCP tools

`ListSkills(projectName)` returns project-local skills **and** global skills visible from that project, with `scope: "project"` or `scope: "global"` on each item. Skills with the same name in the two scopes are listed separately. `ListSkills("@global")` lists only global skills.

`GetSkill(projectName, skillName)` returns project-local content. To read a global skill, use `GetSkill("@global", skillName)`.

`CreateSkill(projectName, skillName, content, overwrite=false)` creates the canonical project-local file. Use `CreateSkill("@global", skillName, content, overwrite=false)` to create or update the workspace-global file. Existing skills are rejected unless `overwrite=true` is supplied. Files are confined to the local workspace; symlink traversal is rejected. The result includes the skill scope, canonical name, workspace-relative or project-relative path, SHA-256 hash, and whether an existing skill was overwritten.

`ListSkills` and `GetSkill` are read-only and require `workspace:read`. `CreateSkill` requires `workspace:write` and follows the same local trust and confirmation model as other structured workspace mutations. Skill names must be a single directory name; rooted and traversal-style paths are rejected. Empty skill content is rejected.

## Security model

Skills are guidance, not capabilities. Instructions inside a skill that require file changes, process execution, Git writes, or other actions must still be carried out through the corresponding Jarvis tools and are subject to their existing permissions, trust level, and confirmation behavior.

`CreateSkill` persists only the skill content supplied by the caller. It does not execute the skill, grant the skill additional authority, or alter the permissions of later commands.

The `@global` name is a reserved project selector for the shared workspace skill scope. The global scope grants no additional execution authority.

## Migrating existing skills

Use `python3 scripts/migrate-skills.py /path/to/workspace` to preview moves from both the workspace-global and individual project `.jarvis/skills/` directories. Add `--apply` to move them into `.agents/skills/`. The utility preserves skill contents and other directory files, skips destination conflicts and symlinks, and never overwrites an existing skill. Review skipped entries and resolve conflicts manually. Read commands never silently mutate old skill locations.
