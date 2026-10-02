# Jarvis Skills

Jarvis skills are project-owned procedural instructions that an agent can discover and read through the MCP surface. They do not execute code, grant permissions, or bypass the normal Jarvis trust model.

## Layout

Store project-local skills under:

```text
<project>/.jarvis/skills/<skill-name>/SKILL.md
```

For compatibility, `skill.md` is also accepted when `SKILL.md` is absent.

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

A skill is ordinary Markdown. The directory name is its Jarvis skill name. `ListSkills` uses the first non-empty, non-heading line as a short discovery description when one is available.

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

`ListSkills(projectName)` returns the available project-local skill names and short descriptions.

`GetSkill(projectName, skillName)` returns the full Markdown content for one skill.

Both tools are read-only and require only `workspace:read`. Skill names must be a single directory name; rooted and traversal-style paths are rejected.

## Security model

Skills are guidance, not capabilities. Instructions inside a skill that require file changes, process execution, Git writes, or other actions must still be carried out through the corresponding Jarvis tools and are subject to their existing permissions, trust level, and confirmation behavior.

The initial implementation is deliberately project-local. Global or workspace-level skill scopes can be added later without changing the basic skill format.
