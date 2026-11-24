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
