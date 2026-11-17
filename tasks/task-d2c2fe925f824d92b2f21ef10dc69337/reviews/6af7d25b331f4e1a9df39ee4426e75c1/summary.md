# Independent correction review

Verdict: Accepted for narrowly scoped correction by source inspection.

Frozen commit `2e4f470177fc76d45d698f11cf6d67566e87f40d` (base `35f675cc1564710d10fdb2f50a03edff39368a28`). Resolves prior ChangesRequested decision 6657 by removing the pending-job early return in `McpToolHelpers.attachAccountGlobal`. First asynchronous `AgentInbox` response now prepends authenticated account-global guidance, instead of omitting it until a later tool call. The new NUnit test asserts policy text precedes pending-job output. Existing account, project, and delivery-cache guards remain. Diff contains 2 production deletions and 25 test additions; no unrelated edits observed.

Independent verification: `dotnet test Server.Tests/Server.Tests.fsproj -c Release --no-restore` returned exit code 0, but the default checkout was `1046d5a4647ef4cdc39f33f94f3d04a5c60d0279`, **not** frozen review commit. This is only ambient regression evidence. Author-reported Release Server.Tests 499/499 was not independently reproduced on exact frozen tree. No merge or deployment.

No blocking problem identified in this correction. Verdict is not a general certification of full cross-machine account-instruction delivery.