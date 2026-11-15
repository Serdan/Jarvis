# Per-project Manager heartbeat: operator runbook

This is a project-scoped orchestration convention on the existing Jarvis
scheduler and ChatGPT browser bridge, **not** a new recursive task manager or
scheduler service. It does not run automatically when installed. The tracked
Jarvis config is disabled by default. Each project keeps its own role
attachment, Manager agent, schedule ID, config and launch journal.

## Current deployment boundary (2026-11-15)

The source tree contains the reviewed general scheduler and the four
ScheduleExecution/ListScheduledExecutions/GetScheduledExecution/
CancelScheduledExecution MCP methods. The connected **deployed** command
catalog currently exposes only the older ScheduleBrowserExecution family.
It therefore cannot create the required AfterAgentIdle schedule yet.
Do not substitute an hourly Every schedule, create live tabs, update the
client/server or restart services without separate operator authorization.
After deployment, inspect the actual connected command catalog again.

The Manager ID is an attribution/coordination label, not a principal.
Browser and process permissions still come from the OAuth user, Jarvis
permission policy, registered window and site host approval.

## Artifacts

- config/manager/Manager.role.md: reusable role body to store as a canonical
  project role named project-manager
- config/manager/Jarvis.json: disabled, tracked project-specific template;
  includes 5 worker and 3 reviewer labels, a hard maximum of 5/3 concurrent
  sessions, and up to 8 decisions per heartbeat
- scripts/manager-heartbeat.mjs: fail-closed planner and durable per-project
  launch reservation journal; no browser effects
- scripts/tests/manager-heartbeat.test.mjs: deterministic fixtures

Runtime-only files live in artifacts/manager/Jarvis (already gitignored).
Never commit the launch journal. Use **one shared journal path per project**
across all Manager invocations and do not use different worktrees' journals
for the same live project.

## Initial registration and role setup (operator-approved rollout)

1. Confirm the browser window registration is live with
   browser_list_windows; confirm ChatGPT is authenticated, the extension is
   paired, chatgpt.com host permission is granted and agent tab creation is
   allowed. Do not hard-code a window ID in version control.
2. Confirm schedule_execution, list_scheduled_executions,
   get_scheduled_execution and cancel_scheduled_execution are all present
   in list_commands on the **deployed** client/server. Also verify that
   the project, Agent activity and challenge facilities required for a
   complete snapshot are accessible. No live launch if any required
   evidence source is missing.
3. In each project's ledger, register the taskless Manager agent if absent
   using register_agent(projectName, agentId="Manager"). Register missing
   worker/reviewer labels before configuring them as eligible. A fresh
   registration is Stopped/Parked; it does not launch a conversation.
4. Create the canonical role with
   create_role(projectName, agentId=<operator actor>,
   roleId="project-manager", purpose="Bounded per-project Manager heartbeat",
   instructions=<exact contents of config/manager/Manager.role.md>).
   Then call attach_role(projectName, agentId=<operator actor>,
   targetAgentId="Manager", roleId="project-manager",
   expectedVersion=<current Manager instruction config version>).
   Use get_agent_instruction_config/resolve_agent_instructions to verify.
   Repeat per project; a role in Jarvis's ledger does not automatically
   exist in Wayfold's ledger.
5. After the implementation passes independent frozen review and exact
   integration, create the ignored runtime config and explicitly enable it
   **only with operator permission**. For example, on the host in Jarvis:

~~~sh
mkdir -p artifacts/manager/Jarvis
cp config/manager/Jarvis.json artifacts/manager/Jarvis/config.json
node -e 'const fs=require("node:fs"); const p="artifacts/manager/Jarvis/config.json"; const c=JSON.parse(fs.readFileSync(p,"utf8")); c.enabled=true; fs.writeFileSync(p,JSON.stringify(c,null,2)+"\n",{mode:0o600});'
node scripts/manager-heartbeat.mjs status --config artifacts/manager/Jarvis/config.json --journal artifacts/manager/Jarvis/journal.json
~~~

The Manager uses the ignored runtime config (not the disabled tracked
template). The config is an operational stop switch. Keep this runtime
config and journal together through ordinary client restarts.

## Authoritative snapshot contract

The Manager gets task_overview at a verified ledger head, observes **all**
configured agents using observe_agent, checks the independent review queue,
task readiness, challenges, and relevant client activity. Include other
working or uncertain agents in the snapshot so their activity counts against
the per-project budget. Never set complete/verified flags by assumption.

The planner accepts JSON in this form (the example is deliberately
incomplete for the full 5/3 Jarvis config; the real file must include all
configured labels):

~~~json
{
  "projectName": "Jarvis",
  "complete": true,
  "agentActivityVerified": true,
  "reviewQueueVerified": true,
  "challengesVerified": true,
  "ledgerHead": "6ab793928fa7f998b9d5796061cf66d3c0b02e40",
  "capturedAtUtc": "2026-11-15T02:00:00Z",
  "unclaimedReviewCount": 1,
  "agents": [
    {
      "agentId": "Manager",
      "kind": "manager",
      "state": "NeedsWork",
      "execution": "Stopped",
      "slotAvailable": true,
      "verified": true,
      "hasReadyAssignment": false,
      "waitingOnChallenge": false,
      "lastAttributedActivityAtUtc": null
    },
    {
      "agentId": "Worker 1",
      "kind": "worker",
      "state": "Ready",
      "execution": "Stopped",
      "slotAvailable": false,
      "verified": true,
      "hasReadyAssignment": true,
      "assignmentKey": "task-123/attempt-456/gen-1",
      "priorityRank": 0,
      "waitingOnChallenge": false,
      "lastAttributedActivityAtUtc": null
    }
  ]
}
~~~

Important: observe_agent reports **state** and **execution** separately.
Ready workers normally have state Ready and execution Stopped; reviewers
available for the shared queue commonly have state NeedsWork and execution
Stopped. A Ready worker must carry an `assignmentKey` made from canonical
task ID, attempt ID and generation; this suppresses stale Ready relaunches
after an observed tool call. The local lastSeenAtUtc field is explicitly
noncanonical and must
not be copied into lastAttributedActivityAtUtc merely because it is recent.
Only a positively observed, project/agent-attributed tool call may populate
lastAttributedActivityAtUtc, with activityEvidence set to
"ExplicitAttributedToolCall". Otherwise leave it null, allowing the durable
reservation to remain uncertain until independently resolved.

Record the actual task/agent/review evidence before setting all four
completeness booleans true. A missing registered agent, missing challenge
query, stale data, contradictory state or incomplete queue read must fail
closed. Captures older than three minutes are rejected.

## One bounded Manager pass

Capture the snapshot at artifacts/manager/Jarvis/snapshot.json. Test without
side effects first:

~~~sh
node scripts/manager-heartbeat.mjs dry-run --config artifacts/manager/Jarvis/config.json --snapshot artifacts/manager/Jarvis/snapshot.json --journal artifacts/manager/Jarvis/journal.json
~~~

When the role has checked the snapshot and live re-observation, reserve:

~~~sh
node scripts/manager-heartbeat.mjs plan --config artifacts/manager/Jarvis/config.json --snapshot artifacts/manager/Jarvis/snapshot.json --journal artifacts/manager/Jarvis/journal.json
~~~

The returned reservations are the **only** approved launch candidates for
that pass. The planner fills workers with Ready assignments first, ordered by
optional verified canonical priorityRank (0 is highest), then eligible
taskless reviewers against the unclaimed frozen review count,
and counts prior pending/uncertain launches and other Working agents against
the 5/3 budget. Duplicate timer ticks reserve nothing twice.

For each reservation, re-observe the agent and review demand immediately
before dispatch. If eligibility changed, mark the reservation blocked and
do not launch. Otherwise atomically **begin** the reserved launch:

~~~sh
node scripts/manager-heartbeat.mjs begin --config artifacts/manager/Jarvis/config.json --journal artifacts/manager/Jarvis/journal.json --launch-id <launch-id>
~~~

`begin` checks the live runtime stop switch, freshness and reservation status,
then durably records `dispatching` *before* the browser side effect. A second
`begin` of the same launch ID is rejected, including across restarts. If the
browser transport fails after `begin`, mark uncertain, never retry the tab.
Use `check` only for read-only diagnostics, never as the dispatch gate. Otherwise invoke
browser_run_in_new_tab with the registered window, url
"https://chatgpt.com/", projectName "Jarvis", scriptPath
"browser-scripts/chatgpt/launchAgent.js", closeTabAfterExecution true,
and argsJson containing a minimal prompt for that one Worker N or
Reviewer N. The Manager role file contains the exact prompt convention.
Do not launch a reviewer by named notification: the reviewer claims from
the shared frozen queue.

Report the actual one-shot browser result with the same launch ID:

~~~sh
node scripts/manager-heartbeat.mjs mark --config artifacts/manager/Jarvis/config.json --journal artifacts/manager/Jarvis/journal.json --launch-id <launch-id> --result submitted
~~~

Use --result uncertain on timeout, transport loss, unexpected UI result
or unverified submission, and --result blocked on a known safe pre-launch
stop. Neither is automatically retried. A successful browser submission is
not proof the agent called Jarvis. Retain reservations until positive
attributed agent activity or explicit operator recovery.

## Start the one Manager idle heartbeat (only after approval)

Once the new scheduler is **deployed and verified**, call schedule_execution
with these exact fields, replacing only the live registered window token and
using the appropriate project:

~~~json
{
  "scheduleId": "manager-jarvis-idle",
  "projectName": "Jarvis",
  "agentId": "Manager",
  "triggerKind": "AfterAgentIdle",
  "actionKind": "BrowserScript",
  "idleAgentId": "Manager",
  "idleSeconds": 900,
  "windowRegistration": "<live registered window>",
  "url": "https://chatgpt.com/",
  "scriptPath": "browser-scripts/chatgpt/launchAgent.js",
  "argsJson": "{\"prompt\":\"You are Manager in Jarvis. Read your Jarvis agent_inbox, resolve the project-manager role, and perform one bounded scheduling pass using docs/ManagerHeartbeat.md. Do not deploy or create another schedule.\"}",
  "active": false,
  "closeTabAfterExecution": true
}
~~~

Do **not** set repeatEverySeconds. The AfterAgentIdle trigger already
re-arms from Manager-attributed Jarvis activity. The scheduler supplies
single-flight dispatch, fresh server activity verification, 30-minute stale
recovery and no uncertain occurrence replay. There must be only one active
Manager schedule **per project** across all devices; the scheduler's local
file lock alone is not cross-device leader election.

## Health check, stop, resume and recovery

Health check: call list_scheduled_executions and
get_scheduled_execution(scheduleId="manager-jarvis-idle"); confirm
AfterAgentIdle/900/BrowserScript, due time, last attempt, error and
single-flight state. Check browser_list_windows, task_overview,
observe_agent("Manager"), and the planner's read-only status:

~~~sh
node scripts/manager-heartbeat.mjs status --config artifacts/manager/Jarvis/config.json --journal artifacts/manager/Jarvis/journal.json
node --test scripts/tests/manager-heartbeat.test.mjs
~~~

Stop: first set runtime enabled to false (so an already-launched Manager
cannot reserve new workers); then call
cancel_scheduled_execution(scheduleId="manager-jarvis-idle",
agentId=<operator actor>). This prevents future launches but does **not**
kill any active agent or undo an already submitted browser action.

Resume: verify deployed tool availability, window registration, account
permissions, scheduler state and journal; preserve all old reservations.
Set runtime enabled to true under operator authorization and create/replace
the same single AfterAgentIdle schedule. Do not create a second schedule
ID or launch a replacement Manager tab merely because its predecessor's
outcome is uncertain.

Uncertain worker launch: inspect the journal, browser launch receipt,
task/agent canonical state and positively attributed activity. Without
positive evidence, retain the reservation; do not retry. An operator may
explicitly clear a **specific** reservation with a durable audit reason:

~~~sh
node scripts/manager-heartbeat.mjs release --config artifacts/manager/Jarvis/config.json --journal artifacts/manager/Jarvis/journal.json --launch-id <launch-id> --reason "Operator verified previous launch cannot be active"
~~~

If journal.json.lock remains after a crash, verify there is no running
planner process or competing Manager before manually removing that lock.
Never automatically steal a lock based only on age. Keep journal.json;
deleting it would erase uncertain-launch evidence and enable duplicates.

## Validation and integration

~~~sh
node --test scripts/tests/manager-heartbeat.test.mjs
dotnet test Client.Tests/Client.Tests.fsproj -c Release
dotnet test Server.Tests/Server.Tests.fsproj -c Release
~~~

Tests never open browser tabs, create schedules, register live agents or
mutate the canonical role. Submit the exact branch commit for independent
frozen review, address findings, then submit exact-commit integration to
Lead. No live rollout, client/server deployment, restart or scheduled
agent launch is implied by passing tests.
