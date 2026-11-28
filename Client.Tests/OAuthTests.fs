module Client.Tests.OAuthTests

open System
open System.IO
open Client
open NUnit.Framework

let private oauthConfig =
    { Domain = "https://auth.example.test"
      Audience = "https://api.example.test"
      ClientId = "client-id"
      RedirectUri = "http://127.0.0.1:43821/callback" }

[<Test>]
let refreshTokensSurviveRestartsWithoutSavingAccessTokens () =
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-oauth-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try
        let path = Path.Combine(dir, "oauth-refresh.json")
        OAuth.saveRefreshTokenAt path oauthConfig "refresh-token-a"
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "refresh-token-a"))
        Assert.That(File.ReadAllText(path), Does.Not.Contain("accessToken"))
        OAuth.saveRefreshTokenAt path oauthConfig "refresh-token-b"
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "refresh-token-b"))
        let otherClient = { oauthConfig with ClientId = "another-client" }
        Assert.That(OAuth.loadRefreshTokenAt path otherClient, Is.EqualTo(None))
        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
            Assert.That(File.GetUnixFileMode(path), Is.EqualTo(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)) 
    finally
        Directory.Delete(dir, true)


[<Test>]
let insecureRefreshTokenCacheIsIgnored () =
    if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
        let dir = Path.Combine(Path.GetTempPath(), "jarvis-oauth-tests-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(dir) |> ignore
        try
            let path = Path.Combine(dir, "oauth-refresh.json")
            OAuth.saveRefreshTokenAt path oauthConfig "private-token"
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead)
            Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(None))
            let link = Path.Combine(dir, "symlink.json")
            File.CreateSymbolicLink(link, path) |> ignore
            Assert.That(OAuth.loadRefreshTokenAt link oauthConfig, Is.EqualTo(None))
        finally
            Directory.Delete(dir, true)


[<Test>]
let wslDetectionRecognizesMicrosoftKernelsOnlyOnLinux () =
    Assert.That(OAuth.isWslRelease true "5.15.167.4-microsoft-standard-WSL2", Is.True)
    Assert.That(OAuth.isWslRelease true "4.4.0-19041-Microsoft", Is.True)
    Assert.That(OAuth.isWslRelease true "6.8.0-generic", Is.False)
    Assert.That(OAuth.isWslRelease false "microsoft", Is.False)

[<Test>]
let authorizationLinkIsPresentedBeforeDesktopLaunchAndWslSkipsLaunch () =
    let uri = Uri("https://auth.example.test/authorize?state=abc&code_challenge=xyz")
    let events = ResizeArray<string>()
    let show = function Some value -> events.Add("link:" + value) | None -> ()
    let launch (_: Uri) = events.Add("launch")
    OAuth.presentAuthorization true show launch uri
    Assert.That(events |> Seq.toList, Is.EqualTo(box [ "link:" + uri.AbsoluteUri ]))
    events.Clear()
    OAuth.presentAuthorization false show launch uri
    Assert.That(events |> Seq.toList, Is.EqualTo(box [ "link:" + uri.AbsoluteUri; "launch" ]))

[<Test>]
let callbackRequiresMatchingPathAndStateAndCodeWithoutError () =
    let valid = OAuth.callbackIsValid "/callback" "secret"
    Assert.That(valid "/callback" "secret" null "code", Is.True)
    Assert.That(valid "/other" "secret" null "code", Is.False)
    Assert.That(valid "/callback" "wrong" null "code", Is.False)
    Assert.That(valid "/callback" "secret" "access_denied" "code", Is.False)
    Assert.That(valid "/callback" "secret" null "", Is.False)

open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks

type private ManualClock() =
    inherit TimeProvider()
    let mutable ticks = 0L
    override _.TimestampFrequency = TimeSpan.TicksPerSecond
    override _.GetTimestamp() = ticks
    member _.Advance(duration: TimeSpan) = ticks <- ticks + duration.Ticks

type private ScriptedHandler(respond: string -> string -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, cancellation) =
        task {
            let! body = request.Content.ReadAsStringAsync(cancellation)
            return respond request.RequestUri.AbsolutePath body
        }

let private response status body =
    let result = new HttpResponseMessage(status)
    result.Content <- new StringContent(body)
    result

let private deviceGrant lifetime interval =
    let intervalField = interval |> Option.map (fun n -> $",\"interval\":{n}") |> Option.defaultValue ""
    $"""{{"device_code":"opaque-device-secret","user_code":"ABCD-EFGH","verification_uri":"https://auth.example.test/activate","expires_in":{lifetime}{intervalField}}}"""

let private tokenResponse access refresh expires =
    $"""{{"access_token":"{access}","refresh_token":"{refresh}","expires_in":{expires}}}"""

let private oauthError error =
    response HttpStatusCode.BadRequest ($"""{{"error":"{error}","error_description":"opaque-server-secret"}}""")

let private withCache run =
    let dir = Path.Combine(Path.GetTempPath(), "jarvis-device-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try run (Path.Combine(dir, "oauth-refresh.json"))
    finally Directory.Delete(dir, true)

let private runEnrollment client path clock delay present cancellation =
    OAuth.enrollDeviceAt client oauthConfig path clock delay present cancellation
    |> fun task -> task.GetAwaiter().GetResult()

let private noPrompt (_: DeviceAuthorizationPrompt) = ()

let private fakeDelay (clock: ManualClock) (waits: ResizeArray<TimeSpan>) duration (ct: CancellationToken) =
    ct.ThrowIfCancellationRequested()
    waits.Add duration
    clock.Advance duration
    Task.CompletedTask

[<Test>]
let deviceEnrollmentUsesExistingScopeAndHonorsServerAndNetworkBackoff () =
    withCache (fun path ->
        let clock = ManualClock()
        let waits = ResizeArray<TimeSpan>()
        let mutable polls = 0
        let mutable prompts = 0
        let handler = new ScriptedHandler(fun endpoint form ->
            match endpoint with
            | "/oauth/device/code" ->
                Assert.That(form, Does.Contain("client_id=client-id"))
                Assert.That(form, Does.Contain("audience=https%3A%2F%2Fapi.example.test"))
                Assert.That(form, Does.Contain("scope=openid+profile+offline_access+client%3Aconnect"))
                Assert.That(form, Does.Not.Contain("client_secret"))
                response HttpStatusCode.OK (deviceGrant 100 None)
            | "/oauth/token" ->
                Assert.That(form, Does.Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code"))
                Assert.That(form, Does.Contain("client_id=client-id"))
                Assert.That(form, Does.Contain("device_code=opaque-device-secret"))
                polls <- polls + 1
                match polls with
                | 1 -> oauthError "authorization_pending"
                | 2 -> oauthError "slow_down"
                | 3 -> raise (HttpRequestException("opaque-network-secret"))
                | 4 -> response HttpStatusCode.ServiceUnavailable "{}"
                | 5 -> response HttpStatusCode.OK (tokenResponse "access-secret" "refresh-secret" 3600)
                | _ -> failwith "Unexpected poll"
            | _ -> failwith "Unexpected endpoint")
        use client = new HttpClient(handler)
        let present prompt =
            prompts <- prompts + 1
            Assert.That(prompt.VerificationUri.AbsoluteUri, Is.EqualTo("https://auth.example.test/activate"))
            Assert.That(prompt.UserCode, Is.EqualTo("ABCD-EFGH"))
            Assert.That(File.Exists path, Is.False)
        runEnrollment client path clock (fakeDelay clock waits) present CancellationToken.None
        Assert.That(prompts, Is.EqualTo(1))
        Assert.That(polls, Is.EqualTo(5))
        Assert.That(waits |> Seq.map _.TotalSeconds |> Seq.toList, Is.EqualTo(box [5.; 5.; 10.; 20.; 40.]))
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "refresh-secret"))
        let persisted = File.ReadAllText(path)
        Assert.That(persisted, Does.Not.Contain("access-secret").And.Not.Contain("opaque-device-secret").And.Not.Contain("ABCD-EFGH")))

[<TestCase("access_denied", "denied")>]
[<TestCase("expired_token", "expired")>]
[<TestCase("invalid_client", "rejected")>]
let terminalDeviceErrorsDoNotOverwriteCredentials error diagnostic =
    withCache (fun path ->
        OAuth.saveRefreshTokenAt path oauthConfig "previous-refresh"
        let clock = ManualClock()
        let waits = ResizeArray<TimeSpan>()
        let mutable polls = 0
        use client = new HttpClient(new ScriptedHandler(fun endpoint _ ->
            if endpoint = "/oauth/device/code" then response HttpStatusCode.OK (deviceGrant 60 (Some 1))
            else
                polls <- polls + 1
                oauthError error))
        let failure = Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
            runEnrollment client path clock (fakeDelay clock waits) noPrompt CancellationToken.None))
        Assert.That(failure.Message, Does.Contain(diagnostic).And.Not.Contain("opaque-server-secret"))
        Assert.That(polls, Is.EqualTo(1))
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "previous-refresh")))

[<Test>]
let pollingStopsAtLocalExpiryWithoutExtraRequest () =
    withCache (fun path ->
        let clock = ManualClock()
        let waits = ResizeArray<TimeSpan>()
        let mutable polls = 0
        use client = new HttpClient(new ScriptedHandler(fun endpoint _ ->
            if endpoint = "/oauth/device/code" then response HttpStatusCode.OK (deviceGrant 6 None)
            else
                polls <- polls + 1
                oauthError "authorization_pending"))
        let failure = Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
            runEnrollment client path clock (fakeDelay clock waits) noPrompt CancellationToken.None))
        Assert.That(failure.Message, Does.Contain("expired"))
        Assert.That(polls, Is.EqualTo(1))
        Assert.That(waits |> Seq.map _.TotalSeconds |> Seq.toList, Is.EqualTo(box [5.; 1.]))
        Assert.That(File.Exists path, Is.False))

[<Test>]
let cancellationDuringPollingLeavesExistingCacheUntouched () =
    withCache (fun path ->
        OAuth.saveRefreshTokenAt path oauthConfig "previous-refresh"
        use cts = new CancellationTokenSource()
        let clock = ManualClock()
        let mutable requests = 0
        use client = new HttpClient(new ScriptedHandler(fun _ _ ->
            requests <- requests + 1
            response HttpStatusCode.OK (deviceGrant 60 None)))
        let delay _ (ct: CancellationToken) =
            cts.Cancel()
            Task.FromCanceled(ct)
        Assert.Catch<OperationCanceledException>(TestDelegate(fun () ->
            runEnrollment client path clock delay noPrompt cts.Token)) |> ignore
        Assert.That(requests, Is.EqualTo(1))
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "previous-refresh")))

[<Test>]
let missingRefreshTokenCannotReportSuccessfulEnrollment () =
    withCache (fun path ->
        let clock = ManualClock()
        let waits = ResizeArray<TimeSpan>()
        use client = new HttpClient(new ScriptedHandler(fun endpoint _ ->
            if endpoint = "/oauth/device/code" then response HttpStatusCode.OK (deviceGrant 60 None)
            else response HttpStatusCode.OK """{"access_token":"only-access","expires_in":3600}"""))
        let failure = Assert.Throws<InvalidOperationException>(TestDelegate(fun () ->
            runEnrollment client path clock (fakeDelay clock waits) noPrompt CancellationToken.None))
        Assert.That(failure.Message, Does.Contain("refresh token"))
        Assert.That(File.Exists path, Is.False))

[<Test>]
let unattendedRestartAndRefreshUseTheSamePersistedCredentials () =
    withCache (fun path ->
        OAuth.saveRefreshTokenAt path oauthConfig "initial-refresh"
        let mutable requests = 0
        use client = new HttpClient(new ScriptedHandler(fun endpoint form ->
            Assert.That(endpoint, Is.EqualTo("/oauth/token"))
            Assert.That(form, Does.Contain("grant_type=refresh_token"))
            Assert.That(form, Does.Contain("client_id=client-id"))
            requests <- requests + 1
            match requests with
            | 1 ->
                Assert.That(form, Does.Contain("refresh_token=initial-refresh"))
                response HttpStatusCode.OK (tokenResponse "access-a" "rotated-a" 1)
            | 2 ->
                Assert.That(form, Does.Contain("refresh_token=rotated-a"))
                response HttpStatusCode.OK (tokenResponse "access-b" "rotated-b" 3600)
            | 3 ->
                Assert.That(form, Does.Contain("refresh_token=rotated-b"))
                response HttpStatusCode.OK (tokenResponse "access-c" "rotated-c" 3600)
            | _ -> failwith "Unexpected request"))
        let logs = ResizeArray<string>()
        let restore () = (OAuth.loginUnattendedAt client oauthConfig path logs.Add).GetAwaiter().GetResult()
        let session = restore()
        Assert.That(session.GetAccessTokenAsync().GetAwaiter().GetResult(), Is.EqualTo("access-b"))
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "rotated-b"))
        let restarted = restore()
        Assert.That(restarted.GetAccessTokenAsync().GetAwaiter().GetResult(), Is.EqualTo("access-c"))
        Assert.That(OAuth.loadRefreshTokenAt path oauthConfig, Is.EqualTo(Some "rotated-c"))
        Assert.That(requests, Is.EqualTo(3))
        Assert.That(String.concat "\n" logs, Does.Not.Contain("initial-refresh").And.Not.Contain("rotated-").And.Not.Contain("access-")))

[<Test>]
let unattendedMissingOrRevokedCredentialsNeverOpenBrowser () =
    withCache (fun path ->
        let mutable requests = 0
        use client = new HttpClient(new ScriptedHandler(fun endpoint _ ->
            Assert.That(endpoint, Is.EqualTo("/oauth/token"))
            requests <- requests + 1
            oauthError "invalid_grant"))
        let restore () =
            (OAuth.loginUnattendedAt client oauthConfig path ignore).GetAwaiter().GetResult() |> ignore
        let absent = Assert.Throws<InvalidOperationException>(TestDelegate restore)
        Assert.That(absent.Message, Does.Contain("reauthorization required"))
        Assert.That(requests, Is.Zero)
        OAuth.saveRefreshTokenAt path oauthConfig "revoked"
        let revoked = Assert.Throws<InvalidOperationException>(TestDelegate restore)
        Assert.That(revoked.Message, Does.Contain("reauthorization required").And.Not.Contain("opaque-server-secret"))
        Assert.That(requests, Is.EqualTo(1)))

[<Test>]
let browserAuthorizationStillUsesPkceAndOfflineScope () =
    let uri = OAuth.authorizationUri oauthConfig "state" "challenge"
    Assert.That(uri.AbsolutePath, Is.EqualTo("/authorize"))
    Assert.That(uri.Query, Does.Contain("response_type=code").And.Contain("code_challenge_method=S256").And.Contain("code_challenge=challenge"))
    Assert.That(uri.Query, Does.Contain("scope=openid%20profile%20offline_access%20client%3Aconnect"))
