# Bounded subprocess lifecycle

This design addresses JRV-2026-11-02 from the November 7 Jarvis deep review.

## Contract

BoundedProcess.run owns the complete lifecycle of a command intended to finish. Its input is a typed command containing executable, argv, checkout directory, overall timeout, optional per-stream output limit, optional standard input, environment changes and start-info configuration. It returns a typed outcome with exit code, timeout status, stdout, stderr and per-stream truncation status. No shell-string concatenation is introduced.

The wall-clock Stopwatch begins before launch. stdout, stderr, optional stdin and immediate process exit run concurrently, and **all four must complete** inside the remaining deadline. A parent process exiting is not sufficient while inherited pipes remain open. A timeout or explicit cancellation triggers descendant-group termination, stream cancellation/closure, bounded cleanup, and a finite result. This fixes the original deadlock: the former RunCommand path waited for the immediate child, then synchronously blocked on the output task results without a deadline.

### OS ownership

- Linux runs the requested executable through the trusted system util-linux setsid binary at /usr/bin/setsid (or /bin/setsid). The child is launched in a separate session/group. On timeout or completion, a negative group PID SIGKILL reaches members even if the original executable exited earlier (Linux via libc; macOS via libSystem). There is no untrusted PATH fallback for locating the group launcher.
- macOS uses an internal JarvisClient group-host entrypoint, since util-linux setsid is not shipped as a default tool. This helper invokes native setsid through macOS libSystem.B.dylib before launching the actual executable. The helper itself is the process-group leader, so its PID remains a valid group target when a spawned command exits early. The helper is launched using the exact current executable path; for a general dotnet host the managed assembly path is supplied, while a framework-dependent apphost or single-file binary is invoked directly. Passing the assembly path to an apphost would incorrectly start a normal Jarvis client.
- Windows uses a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. The managed group-host process waits on a per-command named EventWaitHandle; its parent assigns it to the job before signaling readiness. Only then does the helper launch the requested executable and descendants. The barrier is independent of standard input, preserving inherited stdin, explicit EOF and supplied content. TerminateJobObject is invoked on timeout or normal bounded-command completion, and closing the job handle also cleans up on client crash.

Linux and Windows receive the same bounded termination semantics. The macOS code is cross-compiled by the managed client project but needs a macOS runtime verification before claiming platform-specific smoke-test coverage.

### Bounded output and input

The stdin policy is a typed InheritInput, ClosedInput or TextInput union. Canonical Git ledger commands explicitly close unused input as they did before the refactor; this is necessary to prevent commands accidentally waiting on an inherited terminal. The Windows named-event startup barrier never reads or redirects stdin itself, preserving all three policies.

All redirected streams are handled asynchronously. The collectors drain output even after the configured response prefix fills, avoiding pipe backpressure deadlocks. Captured output is bounded per stream and decoded using the stream's configured/default encoding, preserving existing Windows console-codepage behavior. The returned text is also constrained by the UTF-8 response-byte budget; a cut through a multibyte character can yield replacement text rather than a complete character. Process stdout and stderr share one overall deadline and both are canceled/disposed during cleanup. Supplied standard-input text is explicitly encoded as BOM-less UTF-8 on every platform. Its write and flush participate in the same deadline; a process that never reads stdin cannot cause an unbounded write.

For a timed-out RunCommand/RunProjectTask, the public result remains TimedOut=true and ExitCode=-1 with whatever output was captured; nonzero normal exit is distinguishable. A separate internal cancellation token overload returns a typed cancellation error. Existing tool signatures and command permission checks are unchanged.

The canonical ledger still uses the existing Git timeout of 30 seconds and permits full Git object output (not limited to 64 KiB). Worktree discovery retains 10 seconds with bounded diagnostic output. Project initialization now uses the bounded Git executor with its original 15-second timeout and bounded diagnostic output. Git timeout errors are translated to the existing RemoteUnavailable/validation errors. RunCommand retains the supplied/default timeout and separate per-stream maxOutputBytes behavior.

### Scope boundaries and limits

- The bounded executor is used for RunCommand, RunProjectTask, developer Git commands via ClientShell, worktree discovery, project initialization Git and canonical Git-ledger subprocesses.
- StartJob is deliberately long-lived and retains its explicit cancel mechanism. Clipboard selection-owner helpers, browser/OAuth launch and the client self-updater are intentionally not treated as bounded commands; their lifetimes have different ownership semantics.
- Descendants which deliberately create their own session on Unix can escape the original process group, as is inherent in process-group isolation; this is not an OS sandbox. POSIX process-group cleanup and Windows Job Objects are lifecycle mechanisms, not security sandboxes.
- Process.Start is a synchronous .NET/OS spawn. Its elapsed time is counted by the Stopwatch, but if the operating system itself never returns from process creation, this managed helper cannot forcibly interrupt that kernel call. Once launch returns, the remainder of the execution/drain/cleanup path is bounded by the deadline plus a small fixed cleanup allowance.
- Existing arbitrary-size Git blob reads remain intentionally unbounded in memory; separate deep-review finding JRV-2026-11-04 addresses resource controls for large reads. Never infer output memory caps from a response serialization cap.
- No Jarvis protocol change, ledger schema change, or new server state is required.

## Verification

BoundedProcessTests covers: direct timeouts, a parent exiting while a descendant keeps both stdout/stderr pipes open (and termination of that descendant), successful command completion while a descendant is still alive with separate output pipes, concurrent heavy stdout/stderr under a small byte cap, nonzero exit distinct from timeout, explicit cancellation, stdin-backpressure timeout, closed-stdin EOF, exact UTF-8 supplied-input handling, and native apphost versus dotnet host argument selection. Existing ClientShell, Worktree, ProjectCreation and LedgerStore regression tests exercise the migrated success paths.

Merged-source Linux verification, November 8, 2026: the combined atomic-file, bounded-process, and AgentInbox focused Release suite passed 39/39, including the shared bounded Git executor with ten measured Git launches per inbox; full Jarvis Release verification passed 420/420 Client.Tests and 95/95 Server.Tests. The merged integration retains the AgentInbox OID-based Git-read metrics while making all Git child execution lifecycle-bounded.

Windows Job Object and macOS session-helper behavior have not been run on native operating systems. Task task-4bcca217de1b4045b436db172d42068a records the outstanding native-platform smoke-test gate; do not publish an updated Windows/macOS client distribution on the strength of Linux tests alone. Source integration is not a deployment.
