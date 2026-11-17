# Reviewer 1 — Accepted (P1 manual-grant device/project binding)

**Review:** `_reviews/c2c4f8819d4c44f08826fbb53b2149a1`  
**Exact frozen HEAD:** `8d38fdf3792de24103f72ea7a6ff46472dc53efd`  
**Previous ChangesRequested:** Reviewer 2 seq5681 on `_reviews/a2997d8727d8484e9764919b7fc590a1`, frozen `6e5d041f16265001b84878018dcf9ebb15b363b1`  
**Branch baseline:** `0c505355c9be0714c2c9eb136cb02e5ab2f4bf0b`

## Independent inspection

- The existing-project grant stores a verified canonical project ID and an original device ID before prompting. `ManualGrantStore.Authorize` rejects different devices, mismatched/missing project identities and unrelated capabilities. Prospective creation stays on its original device and binds only after verified canonical creation.
- `McpToolHelpers.requestGrant` resolves the authenticated connected device **before** the approval prompt. The credentialed dispatch resolves current selection, rejects changes from the approved device before dispatch, re-queries Git Health on that same device and sends the granted command to it. `ClientService.SendCommandToUser` honours the explicit device override. The client permission bypass is entered only after server-side grant validation.
- Three added `ManualGrantTests` cover two registered devices and same-named projects with different identities; wrong-device reuse even with an identical identity value; conversation reselection; same-device identity replacement; same-device positive path; prospective creation device pin; absent/malformed Health proof. Previous pending/denied, owner, capability, restart, default-off Work and credential semantics remain.
- Exact checkout verified after tests: `git rev-parse HEAD` = pinned SHA and `git status --porcelain=v1` empty. `git diff --check 0c505355c9be0714c2c9eb136cb02e5ab2f4bf0b 8d38fdf3792de24103f72ea7a6ff46472dc53efd` exit 0.
- Real local .NET 11 Release `Server.Tests`: **312/312 passed** (including new manual grant cases).
- Real local .NET 11 Release focused/adjacent `Client.Tests` selection (PermissionPolicy, ProjectCreation, SignalRDispatch, WorkQueueReservationPermissions, WorkQueueCredential): **50/50 passed**.
- Supplementary full `Client.Tests` was started but canceled after it did not complete in a practical interval; **no full-suite pass is claimed**. The required focused + adjacent client checks passed.

## Verdict

**Accepted.** The prior P1 device A → device B unauthorized reuse is closed at the device-selection, verifier and independently observed canonical project identity boundaries. No new blocking defect identified in this frozen correction.

**Nonblocking follow-up:** The two-device negative tests cover `UserService` selection and `ManualGrantStore.Authorize`, but do not drive two live-mocked `ReceiveManualGrantedCommand` SignalR endpoints end to end. Adding a complete dispatch regression would protect the integration against future routing refactors; current source inspection confirms the guard before bypass.

No source edits, production Scheduler/cutover, hosted Actions, Codex or live host mutation.
