# Opt-in Roslyn code-quality hooks

Jarvis repositories are **unchanged by default**. Code-quality hooks are client adapters, not
arbitrary repository shell commands. They run in the selected registered Git worktree; a
Roslyn LSP session is retained per canonical checkout path, with the official daemon mode
allowing Roslyn to share its backend without mixing client document state.

## Operator approval (required)

A repository's `.jarvis.json` alone cannot authorize loading project files or analyzers.
An operator must explicitly approve the *exact checkout* before starting JarvisClient:

```sh
export JARVIS_APPROVED_CODE_QUALITY_ROOTS="/absolute/path/to/Wayfold"
```

Multiple approved checkouts can be separated by the OS path separator (colon on Linux,
semicolon on Windows). Add worktree roots individually. In particular, approval of the
main checkout does **not** approve its sibling worktrees. Restart JarvisClient after changing
the environment. Without approval, writes matching an enabled after-write hook and
hook-enabled commits are rejected before modifying files/the index. Unrelated file
writes remain available. No approval means no Roslyn process launch.

The client requires the official `roslyn-language-server` global dotnet tool installed,
and the corresponding .NET runtime. It supports a managed DLL launcher through the current
`dotnet` command when the global shim resolves an obsolete system runtime. No repository
configuration may name an executable, argument list, script, package, URL, or shell.

## Example project configuration

Add the `codeQuality` property to the existing `.jarvis.json` without removing
`tasks`:

```json
{
  "codeQuality": {
    "hooks": [
      {
        "enabled": true,
        "name": "format-csharp-after-edit",
        "trigger": "after-write",
        "adapter": "roslyn-format",
        "extensions": [".cs"],
        "autoFix": true,
        "failure": "blocking"
      },
      {
        "enabled": true,
        "name": "format-selected-csharp-at-commit",
        "trigger": "pre-commit",
        "adapter": "roslyn-format",
        "extensions": [".cs"],
        "autoFix": true,
        "failure": "blocking"
      }
    ]
  }
}
```

Each hook is independently enabled. Only the `roslyn-format` adapter is implemented.
The failure policy is `blocking` or `advisory` (default blocking). An advisory
failure is returned in the structured `Hooks.Reports` result; a blocking failure stops the
operation with the hook and path in the error. The output also lists modified files and
formatting edit counts; Roslyn diagnostic messages are best-effort asynchronous snapshots,
not a full compiler/analyzer diagnostic result.

The adapter sends LSP `textDocument/didOpen` or `didChange` and then
`textDocument/formatting` to the persistent official server. It rejects overlapping
edits, invalid UTF-16 positions, binary files and source files larger than 2 MiB.
Edits are published through Jarvis's existing atomic text replacement with optimistic
snapshot verification; a file that changed during formatting is refused rather than
overwritten. A simplified `.editorconfig` handling pass respects `insert_final_newline`
and `end_of_line` for global properties, `[*]`, `[*.cs]` and `[**/*.cs]`
(see the implementation if relying on more complex glob rules). Roslyn itself
loads the broader editorconfig analyzer/formatting options.

## Commit selection behavior

`GitCommit` still requires explicit paths and commits *only those paths* with
`git commit --only -- <paths>`; unrelated staged changes remain staged and excluded.
For enabled pre-commit hooks, Jarvis checks the requested paths before staging.
A selected path with both staged and unstaged changes is rejected as ambiguous **before**
running Git add or any hook; this is intentional instead of accidentally committing
unselected hunks. Jarvis then stages the requested paths, obtains the exact selected changed
pathnames with NUL-delimited Git output, skips deleted files, formats only matching
`.cs` paths and invokes the existing path-limited commit operation. Other source files
and other worktrees are not formatted.

On a successful blocking hook, a formatting fix to a selected file becomes part of its
normal path-limited commit. A hook failure never creates a commit. On a blocking failure
after staging, the index can retain the caller's explicit staged selection; review the
worktree and retry. External non-cooperating writers remain outside Jarvis's advisory
atomic edit lock contract.

## Lifecycle and constraints

When a worktree-scoped resume supplies a validated checkout, Jarvis prewarms the
configured/approved Roslyn session without blocking the resume response. Otherwise
initialization occurs on the first matching hook. Sessions are cleaned up on Git
worktree removal and JarvisClient shutdown. Missing/unapproved hooks do not launch a server.

This implementation covers C# Roslyn formatting; it does **not** auto-fix arbitrary
diagnostics or implement a complete LSP code-action selection service. It does not
modify Wayfold merely by being present in Jarvis. No deployment is part of this change.

## Validation evidence (2026-11-17)

On .NET 11 Release, `dotnet fsi scripts/roslyn-smoke.fsx` passed against two
disposable local C# roots: first session cold 1,540 ms / 9 edits, same session
warm 115 ms / 0 edits, second independent root 272 ms / 5 edits; no
diagnostics were published during the requests. A repeat with a warm daemon
measured 107 ms / 7 ms / 79 ms respectively; both opted-in after-write hooks
passed and enforced distinct checkout-specific `insert_final_newline` settings.

The focused Release NUnit tests passed **23/23**. An unfiltered client-suite run
timed out after 180 seconds without a test verdict; its logs included
activity-persistence permission errors. Do not treat that run as passing.

These checks establish basic two-root formatting and hook behavior, **not**
distinct project/analyzer compilation state, Wayfold small/large parity against
`dotnet format`, or daemon RSS.

## Suggested validation

Run the code-quality NUnit tests, Git selection tests, the full Jarvis client suite,
and an opt-in smoke on a disposable checkout before approving a production project.
For Wayfold, measure one small file, one large file, cold/warm initialization, two
separate worktrees, output stability, daemon RSS and selected-only commit behavior.
