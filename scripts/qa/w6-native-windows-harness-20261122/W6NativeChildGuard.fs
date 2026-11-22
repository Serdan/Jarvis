namespace Jarvis.Testing

open System
open System.Diagnostics
open System.IO
open System.Security.Principal

/// Disposable testhost-only verifier; compiled into generated scratch overlay.
/// Never included in the tracked W6/WQ11 project or invoked on Linux.
module W6NativeChildGuard =
    let private deny reason = raise (UnauthorizedAccessException("W6_TESTHOST_HOLD: " + reason))
    let attest () =
        if not (OperatingSystem.IsWindows()) then deny "Requires actual Windows OS."
        use identity = WindowsIdentity.GetCurrent()
        if isNull identity || isNull identity.User then deny "Missing testhost Windows token SID."
        let expectedSid = Environment.GetEnvironmentVariable("W6_DEDICATED_SID")
        let operatorSid = Environment.GetEnvironmentVariable("W6_OPERATOR_SID")
        let parent = Environment.GetEnvironmentVariable("JARVIS_CLIENT_TEST_DISPOSABLE_PARENT")
        let script = Environment.GetEnvironmentVariable("W6_NATIVE_PREFLIGHT_SCRIPT")
        if [expectedSid; operatorSid; parent; script] |> List.exists String.IsNullOrWhiteSpace then
            deny "Missing parent-to-testhost native source/identity binding."
        if identity.User.Value <> expectedSid || expectedSid = operatorSid then
            deny "Testhost token is not the distinct dedicated OS identity."
        if not (Path.IsPathFullyQualified script) || not (File.Exists script) then
            deny "Preattested source-pinned child preflight script unavailable."
        // The testhost performs its own physical token/profile and scratch ACL
        // check, independent of the launcher/parent. WQ11's native verifier
        // rejects reparse/junction aliases and non-owner scratch access.
        let confirmed = WindowsDisposableGuard.attestScratchParent parent
        WindowsDisposableGuard.assertOwnerOnly confirmed true true
        let start = ProcessStartInfo("dotnet")
        start.UseShellExecute <- false
        start.CreateNoWindow <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.ArgumentList.Add("fsi")
        start.ArgumentList.Add(script)
        start.ArgumentList.Add("--")
        for part in
            ["--phase"; "testhost"; "--scratch-parent"; parent;
             "--dedicated-sid"; expectedSid; "--operator-sid"; operatorSid] do
            start.ArgumentList.Add(part)
        use child = Process.Start(start)
        if isNull child then deny "Could not start isolated native testhost attestation."
        if not (child.WaitForExit(30000)) then
            child.Kill(true)
            deny "Native testhost preflight did not finish."
        let output = child.StandardOutput.ReadToEnd()
        let error = child.StandardError.ReadToEnd()
        if child.ExitCode <> 0 ||
           not (output.Contains("W6_PREFLIGHT_OK phase=testhost", StringComparison.Ordinal)) then
            deny ("Independent native testhost attestation refused: " + error)
