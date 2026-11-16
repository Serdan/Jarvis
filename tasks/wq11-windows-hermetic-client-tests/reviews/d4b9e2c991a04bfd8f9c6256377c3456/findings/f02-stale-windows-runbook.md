# F02 — P2: Windows runbook contradicts the operator's no-hosted-Actions stop

**Frozen source:** `25a452e0ada1b99e286d40051d8438e905df4abd`  
**Location:** `scripts/test-client-hermetic.md:3, 20-29`

The runbook still says Windows acceptance requires the GitHub-hosted ephemeral runner and describes the GitHub Actions workflow as the expected validation route, including a full-suite `--timeout-seconds 900` command. The canonical WQ-11 task explicitly forbids enabling/running/rerunning GitHub-hosted Actions (zero runner credits) and calls for bounded diagnosis on the isolated Kruso self-hosted Windows QA checkout instead of blindly increasing deadlines. A developer following the checked-in instructions could violate the operator stop.

**Required correction:** Update the runbook to mark GitHub-hosted Actions disabled/prohibited until the operator explicitly authorizes them; document only isolated local/self-hosted .NET11 Release validation, the bounded per-test diagnostic plan, exact-SHA evidence requirements, and the current **F03 blocked** status. Document the explicit scratch recovery script and its safety preconditions rather than presenting stale CI acceptance instructions.