module ImportNetworkTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Client
open Client.ImportNetwork
open Client.PermissionPolicy
open Common
open NUnit.Framework

type private Handler(respond: HttpRequestMessage -> HttpResponseMessage) =
    inherit HttpMessageHandler()
    member val Requests = 0 with get, set
    override this.SendAsync(request, _) =
        this.Requests <- this.Requests + 1
        Task.FromResult(respond request)

type private DelayedHeaders() =
    inherit HttpMessageHandler()
    override _.SendAsync(_, cancellation: CancellationToken) =
        task {
            do! Task.Delay(Timeout.InfiniteTimeSpan, cancellation)
            return new HttpResponseMessage(HttpStatusCode.OK)
        }

type private DelayedBody() =
    inherit MemoryStream()
    override _.ReadAsync(_buffer: Memory<byte>, token: CancellationToken) =
        ValueTask<int>(task {
            do! Task.Delay(Timeout.InfiniteTimeSpan, token)
            return 0
        })

let private success (content: string) =
    let r = new HttpResponseMessage(HttpStatusCode.OK)
    r.Content <- new ByteArrayContent(Encoding.UTF8.GetBytes(content))
    r

let private redirect (location: string) =
    let r = new HttpResponseMessage(HttpStatusCode.Found)
    r.Headers.Location <- Uri(location, UriKind.RelativeOrAbsolute)
    r

let private makeHttp callback =
    let handler = new Handler(callback)
    new HttpClient(handler, disposeHandler = true), handler

let private withDownloaded http trust url limit (token: CancellationToken) =
    let stage = Path.Combine(Path.GetTempPath(), "jarvis-secure-import-" + Guid.NewGuid().ToString("N"))
    try
        let result =
            downloadTo http trust url stage limit token
            |> fun task -> task.GetAwaiter().GetResult()
        result, (if File.Exists stage then Some(File.ReadAllBytes stage) else None)
    finally if File.Exists stage then File.Delete stage

let private known = "https://files.oaiusercontent.com/file-123?X-Amz-Signature=secret"
let private importCommand url =
    ImportFileCommand
        { ProjectName = "Project1"
          FilePath = "download.txt"
          DownloadUrl = url
          FileId = "file-supplied"
          FileName = Some "download.txt"
          MimeType = Some "text/plain"
          Overwrite = false
          CreateParents = false
          MaxBytes = Some 200L }

[<Test>]
let trusted_provider_auto_approves_under_partial_only () =
    clearGrants()
    Assert.That(evaluateWithTrust PartialTrust (importCommand known) |> Result.isOk, Is.True)
    Assert.That(evaluateWithTrust NoTrust (importCommand known) |> Result.isError, Is.True)

[<Test>]
let generic_origin_requires_exact_local_permission () =
    clearGrants()
    let command = importCommand "https://example.org/file?private-token=verysecret"
    let required =
        match evaluateWithTrust PartialTrust command with
        | Error(Client.ConfirmationRequired request) ->
            Assert.That(request.Summary, Does.Contain("example.org"))
            Assert.That(request.Summary, Does.Not.Contain("verysecret"))
            Assert.That(String.concat " " request.Args, Does.Not.Contain("private-token"))
            request
        | other -> failwith $"Expected local confirmation, got {other}"
    grant command required None |> ignore
    Assert.That(evaluateWithTrust PartialTrust command |> Result.isOk, Is.True)
    Assert.That(evaluateWithTrust PartialTrust (importCommand "https://example.org/other") |> Result.isError, Is.True)
    clearGrants()
    Assert.That(evaluateWithTrust FullTrust command |> Result.isOk, Is.True)

[<Test>]
let origin_classification_denies_spoofed_domains_and_nonstandard_ports () =
    Assert.That(isKnownAttachment known, Is.True)
    Assert.That(isKnownAttachment "https://cdn.files.oaiusercontent.com/x", Is.True)
    Assert.That(isKnownAttachment "https://files.oaiusercontent.com.evil.example/x", Is.False)
    Assert.That(isKnownAttachment "https://files.oaiusercontent.com:8443/x", Is.False)
    Assert.That(isKnownAttachment "https://example.net/?file_id=file-123", Is.False)

[<Test>]
let invalid_local_and_credential_urls_reject_before_fetch () =
    for url in
        [ "http://files.oaiusercontent.com/x"
          "https://user:pass@files.oaiusercontent.com/x"
          "https://127.0.0.1/metadata"
          "https://[::1]/secrets"
          "https://169.254.169.254/latest/meta-data"
          "https://files.oaiusercontent.com/x#fragment" ] do
        Assert.That(parseUrl url |> Result.isError, Is.True, $"Unexpected accepted URL: {url}")

[<TestCase("8.8.8.8", true)>]
[<TestCase("1.1.1.1", true)>]
[<TestCase("10.0.0.1", false)>]
[<TestCase("192.168.1.1", false)>]
[<TestCase("172.24.1.1", false)>]
[<TestCase("127.0.0.1", false)>]
[<TestCase("169.254.169.254", false)>]
[<TestCase("100.100.100.200", false)>]
[<TestCase("198.18.0.1", false)>]
[<TestCase("203.0.113.8", false)>]
[<TestCase("::1", false)>]
[<TestCase("fc00::1", false)>]
[<TestCase("fe80::1", false)>]
[<TestCase("2001:db8::1", false)>]
[<TestCase("2002:0a00:0001::", false)>]
[<TestCase("2001:4860:4860::8888", true)>]
[<TestCase("::ffff:127.0.0.1", false)>]
let public_address_policy (text: string) (expected: bool) =
    Assert.That(isPublicAddress(IPAddress.Parse text), Is.EqualTo(expected))

[<Test>]
let rebinding_cannot_change_the_pinned_connection_ip () =
    let mutable resolutions = 0
    let mutable address = ""
    let resolver _ _ =
        resolutions <- resolutions + 1
        Task.FromResult(if resolutions = 1 then [| IPAddress.Parse "1.1.1.1" |]
                        else [| IPAddress.Loopback |])
    let connector ip _ _ =
        address <- ip.ToString()
        Task.FromResult(new MemoryStream() :> Stream)
    use stream =
        resolveAndConnect resolver connector "rebind.example" 443 CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()
    Assert.That(resolutions, Is.EqualTo(1))
    Assert.That(address, Is.EqualTo("1.1.1.1"))

[<Test>]
let mixed_private_and_public_dns_is_denied_before_connect () =
    let mutable connected = false
    let resolver _ _ = Task.FromResult([| IPAddress.Parse "8.8.8.8"; IPAddress.Parse "10.0.0.2" |])
    let connector _ _ _ =
        connected <- true
        Task.FromResult(new MemoryStream() :> Stream)
    Assert.Throws<HttpRequestException>(fun () ->
        resolveAndConnect resolver connector "mixture.example" 443 CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult() |> ignore) |> ignore
    Assert.That(connected, Is.False)

[<Test>]
let trusted_attachment_streams_to_bounded_file () =
    let http, handler = makeHttp (fun _ -> success "hello from ChatGPT")
    use http = http
    let result, bytes = withDownloaded http ProviderOriginsOnly known 128L CancellationToken.None
    match result with
    | Ok receipt ->
        Assert.That(receipt.BytesWritten, Is.EqualTo(18L))
        Assert.That(receipt.Sha256, Does.StartWith("sha256:"))
        Assert.That((bytes = Some(Encoding.UTF8.GetBytes "hello from ChatGPT")), Is.True)
        Assert.That(handler.Requests, Is.EqualTo(1))
    | Error error -> Assert.Fail($"Trusted import rejected: {error}")

[<Test>]
let trusted_attachment_can_redirect_to_another_provider_origin () =
    let http, handler = makeHttp(fun req ->
        if req.RequestUri.AbsolutePath = "/redirect" then redirect "https://cdn.oaiusercontent.com/result"
        else success "complete")
    use http = http
    let result, bytes =
        withDownloaded http ProviderOriginsOnly "https://files.oaiusercontent.com/redirect" 64L CancellationToken.None
    Assert.That(result |> Result.isOk, Is.True)
    Assert.That((bytes = Some(Encoding.UTF8.GetBytes "complete")), Is.True)
    Assert.That(handler.Requests, Is.EqualTo(2))

[<Test>]
let attachment_redirect_outside_provider_is_denied () =
    let http, handler = makeHttp (fun _ -> redirect "https://unrelated.example/path?secret=yes")
    use http = http
    let result, _ = withDownloaded http ProviderOriginsOnly known 64L CancellationToken.None
    match result with
    | Error(ValidationError message) -> Assert.That(message, Does.Contain("trusted file-delivery"))
    | other -> Assert.Fail($"Expected redirect rejection, got {other}")
    Assert.That(handler.Requests, Is.EqualTo(1))

[<Test>]
let public_origin_redirect_to_private_ip_is_denied_before_next_request () =
    let http, handler = makeHttp (fun _ -> redirect "https://127.0.0.1/admin")
    use http = http
    let result, _ = withDownloaded http AnyPublicHttps "https://example.org/data" 64L CancellationToken.None
    Assert.That(result |> Result.isError, Is.True)
    Assert.That(handler.Requests, Is.EqualTo(1))

[<Test>]
let a_redirect_loop_is_bounded () =
    let http, handler = makeHttp (fun _ -> redirect "/again")
    use http = http
    let result, _ = withDownloaded http AnyPublicHttps known 100L CancellationToken.None
    Assert.That(result |> Result.isError, Is.True)
    Assert.That(handler.Requests, Is.EqualTo(6))

[<Test>]
let content_length_and_actual_stream_limits_are_enforced () =
    let http, _ = makeHttp (fun _ ->
        let response = success "hello"
        response.Content.Headers.ContentLength <- Nullable 500L
        response)
    use http = http
    let result, bytes = withDownloaded http AnyPublicHttps known 3L CancellationToken.None
    Assert.That(result |> Result.isError, Is.True)
    Assert.That(bytes.IsNone, Is.True)
    let http2, _ = makeHttp (fun _ ->
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        // StreamContent does not advertise Content-Length, forcing the
        // downloader's per-chunk budget rather than its header shortcut.
        response.Content <- new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes "0123456789"))
        response)
    use http2 = http2
    let result2, _ = withDownloaded http2 AnyPublicHttps known 5L CancellationToken.None
    Assert.That(result2 |> Result.isError, Is.True)

[<Test>]
let imported_stage_is_private_from_the_moment_of_creation () =
    if not (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) then
        Assert.Ignore("Unix mode check requires a Unix OS.")
    let path = Path.Combine(Path.GetTempPath(), "jarvis-secure-stage-" + Guid.NewGuid().ToString("N"))
    let http, _ = makeHttp (fun _ -> success "sensitive contents")
    use http = http
    try
        let result =
            downloadTo http ProviderOriginsOnly known path 100L CancellationToken.None
            |> fun task -> task.GetAwaiter().GetResult()
        Assert.That(result |> Result.isOk, Is.True)
        Assert.That(File.GetUnixFileMode path,
                    Is.EqualTo(UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
    finally if File.Exists path then File.Delete path

[<Test>]
let remote_exception_does_not_expose_signed_query_token () =
    let signature = "X-Amz-Signature=super-private"
    let http, _ = makeHttp (fun _ -> raise (InvalidOperationException("https://files.oaiusercontent.com/a?" + signature)))
    use http = http
    let result, _ = withDownloaded http AnyPublicHttps known 100L CancellationToken.None
    match result with
    | Error(ValidationError message) ->
        Assert.That(message, Does.Not.Contain("super-private"))
        Assert.That(message, Does.Not.Contain("X-Amz"))
    | other -> Assert.Fail($"Unexpected error shape: {other}")

[<Test>]
let HTTPS_to_HTTP_redirect_downgrade_is_rejected () =
    let http, handler = makeHttp (fun _ -> redirect "http://example.org/download")
    use http = http
    let result, _ = withDownloaded http AnyPublicHttps known 100L CancellationToken.None
    Assert.That(result |> Result.isError, Is.True)
    Assert.That(handler.Requests, Is.EqualTo(1))

[<Test>]
let private_DNS_answer_is_rejected_before_any_connect () =
    let mutable connected = false
    let resolver _ _ = Task.FromResult([| IPAddress.Parse "169.254.169.254" |])
    let connector _ _ _ =
        connected <- true
        Task.FromResult(new MemoryStream() :> Stream)
    Assert.Throws<HttpRequestException>(fun () ->
        resolveAndConnect resolver connector "rebound.example" 443 CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult() |> ignore) |> ignore
    Assert.That(connected, Is.False)

[<Test>]
let stalled_headers_and_response_bodies_are_cancellable () =
    use headerClient = new HttpClient(new DelayedHeaders())
    use cancelHeader = new CancellationTokenSource(TimeSpan.FromMilliseconds 120.)
    let first, _ = withDownloaded headerClient AnyPublicHttps known 100L cancelHeader.Token
    Assert.That(first |> Result.isError, Is.True)
    let http, _ = makeHttp (fun _ ->
        let response = new HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new StreamContent(new DelayedBody())
        response)
    use http = http
    use cancelBody = new CancellationTokenSource(TimeSpan.FromMilliseconds 120.)
    let second, _ = withDownloaded http AnyPublicHttps known 100L cancelBody.Token
    Assert.That(second |> Result.isError, Is.True)
