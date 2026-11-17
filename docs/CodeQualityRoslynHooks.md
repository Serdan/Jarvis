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

For isolated integration-test runners whose HOME does not contain the global
dotnet tool store, the operator can set `JARVIS_ROSLYN_LSP_DLL` to the full path
of an installed `roslyn-language-server.dll`. This selects only the Roslyn
managed entrypoint; project files cannot configure or override its location.
The live selected-file pre-commit NUnit test runs only when this variable is set.

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
failure is returned in the structured `Hooks.Reports` result; a blocking pre-commit
failure stops the commit with the hook and path in the error. An **after-write**
formatting failure cannot erase a successful original write: it returns a
structured `Hooks` result with `WritePersisted = true`, an observed
`PersistedAfterHash` when available, and `BlockingFailures`. Callers must
inspect that outcome rather than blindly retrying the write. The output lists modified files and
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

On the supported `agent_inbox` Ready-to-Working transition, Jarvis **queues**
source binding discovery off the TaskRead response path. The inbox does not
wait for Git worktree enumeration, registered-selector resolution, or Roslyn
initialization. Background discovery resolves the focused attempt's exact
source branch to one non-main Git-registered worktree and validates its selector.
Unknown or ambiguous source refs never select main, and may retry on a later
inbox read. In-flight discovery and successful prewarm are deduplicated by
project, agent, attempt, generation and source ref. Before Git enumeration,
a filesystem-only gate matches an enabled, operator-approved checkout's Git
common directory to the *selected project's* main Git directory. Approval of
another project's hooked worktree or of the main checkout alone does not
authorize discovery, let alone a Roslyn process. The later exact registered
source-ref selector and checkout-specific approval remain mandatory; missing
metadata fails closed. Initialization otherwise occurs on the first matching
hook. Sessions are cleaned up on Git worktree removal and JarvisClient shutdown.
Missing/unapproved hooks do not launch a server.

A configured pre-commit C# hook does not require approval for a nonmatching
single-file commit (such as `README.md`); directory selections remain
conservatively gated until their actual contents are enumerated.

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

The post-review focused Release suite passed **42/42** with an explicitly
selected installed `JARVIS_ROSLYN_LSP_DLL`. The live test verified that a
selected-file pre-commit formatting fix landed in the commit while unrelated
staged content stayed excluded. It also covered advisory versus blocking
pre-commit failures and recovery after a broken Roslyn entrypoint and session
disposal. These checks do not establish full acceptance.

An unfiltered Release client-suite rerun timed out after 240 seconds without
a verdict, with activity-persistence UnauthorizedAccessException and
InvalidDataException diagnostics. Do not claim an unfiltered-suite pass.

After independent review 7109 requested correction of the synchronous inbox
lookup, the dispatch was changed to an asynchronous deduplicated work queue,
with an operator-approval/configuration negative gate that avoids Git discovery
when no eligible hook exists. The new dispatcher regression checks passed
**13/13** under Release .NET 11. The final combined Release test filter
(`CodeQualityHooksTests|GitCommandTests|WorktreeTests|DispatchProbeTests`)
passed **58/58, zero skips**, with JARVIS_ROSLYN_LSP_DLL set to the installed
official Roslyn 5.13.0-1.26514.10 DLL (exit 0; 4 seconds). Without that
explicit DLL path, the combined filter passed 56 and skipped two live tests.
The full client suite and Wayfold-specific acceptance measurements remain open.



The follow-up mixed-project negative-gate correction is isolated to the reviewed
feature branch. Its .NET 11 Release focused `CodeQualityHooksTests|SignalRDispatchTests`
filter passed **18, skipped 1, failed 0**; the skipped live Roslyn recovery
case requires `JARVIS_ROSLYN_LSP_DLL`. The test models two independent Git
common-directory identities and confirms that a hooked, approved project A
cannot make project B eligible for background discovery. This is **not** a
production TaskRead-to-inbox integration benchmark, nor does it resolve the
unfiltered-suite or Wayfold-specific acceptance gates.

## Suggested validation

Run the code-quality NUnit tests, Git selection tests, the full Jarvis client suite,
and an opt-in smoke on a disposable checkout before approving a production project.
For Wayfold, measure one small file, one large file, cold/warm initialization, two
separate worktrees, output stability, daemon RSS and selected-only commit behavior.
