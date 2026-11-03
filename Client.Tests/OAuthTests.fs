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
