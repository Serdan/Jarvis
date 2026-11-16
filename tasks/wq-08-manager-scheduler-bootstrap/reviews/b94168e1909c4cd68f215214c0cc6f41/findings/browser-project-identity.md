# P2 — Browser evidence from a different project is reported as belonging to this launch

**Location:** `Client/Modules/WorkQueueSchedulerHostPreview.fs`, in the `ReadOriginal` match around lines 300–310, frozen SHA `fccfe5c3454cace34124b9f71c00dc5d4e081163`.

**Observed:** The successful branch checks only `entry.Intent.LaunchId = launch.Id`. The typed `OriginalBrowserIntent` also contains `ProjectId`, `WorkId`, `WorkGeneration`, `RoleId` and `RoleVersion`. An injected `ReadOriginal` can return an entry with the same launch ID but a different project ID, and `previewWith` will present its `Accepted` receipt (or `DefinitiveNoEffect`) as the current launch's original browser outcome, without a `BrowserIdentityConflict`. The new test exercises a different **launch** ID only, so it does not cover this case.

**Impact:** The default-off diagnostic does not dispatch anything, but its purpose is to surface trustworthy original evidence for manual recovery. Misattributed outcomes may mislead reconciliation and conceal a foreign original browser marker.

**Requested bounded correction:** Require the browser entry's project ID to match the launch's project identity as well as its launch ID before displaying its outcome as belonging to this launch; keep the original operation ID visible in the conflict branch. Check the existing role/work identity if it is straightforward from the launch's pinned data, without adding a global proof framework. Add a regression with **same launch ID, foreign project ID**, asserting `BrowserIdentityConflict`, the preserved operation ID, and no `Accepted` receipt.

**Validation:** The patch's 10 focused .NET 11 Release Client tests independently passed at the frozen commit, but none tests this identity combination. No production dispatch was enabled.