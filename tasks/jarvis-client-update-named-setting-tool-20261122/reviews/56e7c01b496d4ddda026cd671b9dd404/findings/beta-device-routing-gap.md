# F1 — Beta selected-device routing is not positively verified (P1)

**Frozen source:** `5f5448183c239fdde4f5f2953ceba0aad382d17c`  
**Path:** `Client.Tests/NamedSettingTwoNative/TwoNativeNamedSettingTests.fs:166-188`  
**Original requirement:** authenticated two-native-client selected-device routing and account/device isolation, including verified persistence/readback of the intended target.

The sole Beta-targeted tool call is a `machine-name/read` at line 167 and checks only `IsError != true`, not the response's device-specific value. Every Set/Unset and stale-CAS mutation uses Alpha (lines 168-186, 205); Beta's persisted state is asserted unchanged, but is never updated through the tool.

**Concrete false-positive counterexample (source reasoning, not an executed mutant):** an incorrectly implemented selector that sends requests for `deviceId=beta` to Alpha's connection still returns a successful read and meets all current assertions. Even a second native Beta process being alive does not exercise positive Beta tool routing. Therefore the successful 1/1 test does not verify the claimed bidirectional target selection.

**Bounded repair within the four new test/harness paths:** perform a Beta-targeted Set (and optionally Unset) with Beta's own revision through `McpToolHelpers.updateNamedSetting`, assert exact Beta on-disk/read response and Alpha revision/data unchanged, then test that one stale-target or wrong-connection mutation is rejected without falling back to the other process. Preserve the test-only scope; no production or shared fsproj edits are required.

**Impact:** acceptance evidence could hide cross-device routing regressions. This finding concerns completeness of the submitted two-client test evidence, not a proven defect in the product implementation.