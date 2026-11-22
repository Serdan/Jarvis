// Immutable source custody. Never replace these with dirty QA worktree files.
export const pins = Object.freeze({
  base: "dcac884fc061869572b12a03befc12f11c009f2f",
  w6: Object.freeze({
    commit: "bfde56bcc79cedf923d8eaff7ea66cd0af488f23",
    review: "0f4376c3a15d4a2282ecb2de61320bcb",
    paths: Object.freeze({
      "Client/Modules/OrchestrationApi.fs": "069ad69f735797e2682dc2520e4df2f06387756e",
      "Client.Tests/ReviewWorkflowTests.fs": "336bf67d10ccb7d6037a6e17b53ce89a4bdd3795",
      "Client.Tests/HermeticHarnessTests.fs": "cc57136fc0a5ecaedf20f946d01ab54f7e08e776",
      "Client.Tests/Client.Tests.fsproj": "bd0a3e5acb3ab4c44e99f7ffcf896c2ec042aef2",
      "scripts/test-client-hermetic.fsx": "32f3f74029eed32eb929b6d6ff8bb1ef571b0f16"
    })
  }),
  wq11: Object.freeze({
    commit: "e0043d41443669a3aafe11b718bef45c4af992e6",
    review: "0eb6832b38624a42a36fb43874091a5f",
    paths: Object.freeze({
      "Client.Tests/HermeticWindowsGuard.fs": "fa31bedb9276995eb5e2a264e5cd8372c2d00b9d",
      "Client.Tests/HermeticHarnessTests.fs": "7b3e74707ed9b11c15b213247b46556efa3f8544",
      "Client.Tests/Client.Tests.fsproj": "f51378076f34402fd6da3b7dcd1ce37f06918bb4",
      "scripts/test-client-hermetic.fsx": "5c8e9b48f0f6adfe9861a887fd28dea5b4d7e862"
    })
  })
});
export const nativeMatrix = Object.freeze([
  ["same-UUID cross-process exclusion", "PendingIntentExactLocksAllowUnrelatedUUIDsWithinLegacyStripe"],
  ["same-3-character-prefix unrelated UUID concurrency", "PendingIntentExactLocksAllowUnrelatedUUIDsWithinLegacyStripe"],
  ["killed-owner recovery, uncertain intent no callback replay", "PendingIntentNamedMutexRecoversAfterOwnerProcessExits"],
  ["exact UUID lock identity and no residual artifact", "PendingNamedMutexIdentityAndHandleCleanupLeaveNoLockArtifacts"],
  ["protected journal junction/symlink negative must not Ignore", "PendingNamedMutexRejectsSymlinkJournalBeforeCallback"],
  ["ambiguous IOException callback executes once only", "PendingLockNeverRetriesAnAmbiguousIOExceptionCallback"],
  ["ambiguous I/O uncertain ordinary replay denied", "PendingLockRefusesOrdinaryReplayAfterAmbiguousIOException"],
  ["lost response finds accepted remote operation", "LostResponseRecoversAcceptedRemoteOperationButNeverGuessesFailure"],
  ["real unmodified default 60-second WaitOne timeout", "W6NativeDefault60SecondWait"]
]);
