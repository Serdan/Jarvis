# P2 — Source ref check accepts strings invalid as Git refs

**Frozen source:** `ecabbf023a6b750c2c15c92711048712de40f590`, `Server/WorkSnapshotCodec.fs:121-129`.

`WorkSnapshotSource.Ref` only requires the `refs/` prefix, bounded length, no whitespace, and no `..`. For example `refs/heads/a:b` satisfies all these checks, but `git check-ref-format refs/heads/a:b` returned exit code 1 during independent review. Other forbidden Git ref characters and control bytes remain possible.

The codec will therefore produce canonical, checksummed backup provenance that cannot correspond to a fetchable source ref, weakening restore/recovery evidence.

**Required:** Validate Git ref syntax using a faithful bounded rule set or only accept typed, verified source refs from the Git authority boundary; add a negative case for colon and control characters. Keep the immutable commit SHA validation.
