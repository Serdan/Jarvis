# Reviewer decision: Request changes

Reviewed exact commit `23a6a5582f238d40b1a9a5fa446513c72682f6bc`, base `6d1818c58899ad3742e270c6c310514befcbc09e`.

Zero-version arguments resolve against current Git ledger state, and explicit nonzero assertions still work. However, no execution-context fence prevents a stale session from completing/checkpointing/yielding a *new generation of the same task and owner*. The previous mandatory exact version/generation arguments prevented that stale operation. **Blocking finding:** `findings/stale-session-reissue.md`.

Protect current execution-context identity for omitted versions without forcing three manual version arguments. Add regression for stale old session after same-task administrative recovery/new-session resume; verify current session succeeds and retries are idempotent. Resubmit after correction. 342 client/75 server tests passed but the new test exercises only the ordinary path.