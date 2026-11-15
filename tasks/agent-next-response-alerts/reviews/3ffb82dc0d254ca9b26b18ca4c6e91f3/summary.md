# Independent frozen review: ACCEPT

**Request:** 3ffb82dc0d254ca9b26b18ca4c6e91f3  
**Task:** agent-next-response-alerts  
**Frozen head:** 4512f0742cccbed2f53a4ee3094b21d6d57dbafe  
**Base:** a709027e9cf97d0dde0518887e6bd61a4c77800b

## Scope and conclusions

Reviewed the exact frozen production diff and new tests covering the durable per-agent response-alert queue, delivery at response boundaries, promoted tool jobs and completed polls, administrative eligibility, and MCP presentation. The previous promoted-job alert replay/stranding problem is addressed: background completion does not consume alerts; an actual pending-handle reply, poll, or unrelated eligible reply does. Completed-job polling filters old embedded one-shot alert instructions without discarding the immutable terminal result or agent state evidence. Git CAS makes consumption best-effort at-most-once across clients. STOP alerts precede other response guidance.

**No blocking defects identified.**

## Independent validation

- Client .NET 11 Debug focused response-alert / promoted-job tests: **7 passed, 0 failed**
- Client .NET 11 Release focused response-alert tests: **5 passed, 0 failed**
- Server .NET 11 Debug MCP rendering / poll tests: **4 passed, 0 failed**
- `git diff --check a709027e 4512f074`: clean
- Test changes and `docs/ResponseAlerts.md` inspected alongside production changes.

## Explicit limitations

As documented, delivery is **at-most-once**, not guaranteed: a transport failure after the consume commit can lose a one-shot STOP. The queue/clear MCP tools authorize via shared `git:write` user scope rather than a separate Lead-only role; `IssuedBy` is recorded as `AuthenticatedUser`. This suffices for an authenticated-operator workflow, but any future requirement for exclusive Lead actor authority needs a separate authorization and attribution design.

**Verdict: Accept.**