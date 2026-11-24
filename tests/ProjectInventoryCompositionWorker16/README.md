# Disposable same-CAS inventory fixture

This is an isolated test fixture, not a production inventory authority or runtime integration. The empty baseline is an explicit assumption valid only for the new test repository with no other writers or sessions. The modeled fence is not authenticated and cannot fence a real sender. No release, send, terminal evidence, legacy or manual sender, or complete historical baseline is implemented. A matching transaction replay does not authorize dispatch.

The fixture composes W21's independently accepted `abef95dc790ce6f95c51968e8cfb37203c431887`, review `a88d328404034b2a837cd4646ef2b913`, `Client/Modules/ProjectCapacityAdmission.fs`. The project deliberately takes this file as an explicit build input rather than copying or modifying W21-owned source. From the repository root, reproduce against that exact source:

```sh
tmpdir=$(mktemp -d)
git show abef95dc790ce6f95c51968e8cfb37203c431887:Client/Modules/ProjectCapacityAdmission.fs > "$tmpdir/ProjectCapacityAdmission.fs"
dotnet test tests/ProjectInventoryCompositionWorker16/ProjectInventoryCompositionWorker16.fsproj -c Release -p:CapacitySource="$tmpdir/ProjectCapacityAdmission.fs"
rm -rf "$tmpdir"
```

The tests use local disposable Git repositories. They check absent and stale inventory, input-bound replay, competing clients and a fence change during CAS retry. These tests exercise the source-level composition only; they do not show that unsupported send paths are blocked or that actual production occupancy is complete.
