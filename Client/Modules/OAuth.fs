namespace Client

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type OAuthConfig =
    { Domain: string
      Audience: string
      ClientId: string
      RedirectUri: string }

type DeviceAuthorizationPrompt =
    { VerificationUri: Uri
      UserCode: string }

type private OAuthTokenRequestFailure(status: HttpStatusCode, reauthorizationRequired: bool) =
    inherit Exception($"OAuth token request failed with HTTP {int status}.")
    member _.ReauthorizationRequired = reauthorizationRequired

type private OAuthTokenSet =
    { AccessToken: string
      RefreshToken: string option
      ExpiresAt: DateTimeOffset }

type OAuthSession internal (getAccessToken: unit -> Task<string>) =
    member _.GetAccessTokenAsync() = getAccessToken ()

module OAuth =
    [<Literal>]
    let private DefaultDomain = "dev-kn4j3jz3qv2cvw05.eu.auth0.com"

    [<Literal>]
    let private DefaultAudience = "https://jarvis2.kehlet.dev"

    [<Literal>]
    let private DefaultClientId = "QQ6htYawn48RWQjHwh6BZfwaDKrMEtao"

    [<Literal>]
    let private DefaultRedirectUri = "http://127.0.0.1:43821/callback"

    let private http = new HttpClient()
    // Never redirect a request carrying a device code or refresh credential.
    let private deviceHttp = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))
    let private scope = "openid profile offline_access client:connect"

    let private environmentOrDefault name fallback =
        match Environment.GetEnvironmentVariable(name) with
        | null
        | "" -> fallback
        | value -> value.Trim()

    let configuration () =
        { Domain = environmentOrDefault "JARVIS_AUTH0_DOMAIN" DefaultDomain
          Audience = environmentOrDefault "JARVIS_OAUTH_AUDIENCE" DefaultAudience
          ClientId = environmentOrDefault "JARVIS_OAUTH_CLIENT_ID" DefaultClientId
          RedirectUri = environmentOrDefault "JARVIS_OAUTH_REDIRECT_URI" DefaultRedirectUri }

    let private issuer (config: OAuthConfig) =
        let domain = config.Domain.Trim().TrimEnd('/')

        if domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
            domain
        else
            "https://" + domain

    let private requireSecureIssuer config =
        match Uri.TryCreate(issuer config, UriKind.Absolute) with
        | true, uri when (uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback))
                         && String.IsNullOrEmpty(uri.UserInfo) -> ()
        | _ -> invalidOp "Device and unattended authentication require an HTTPS issuer (HTTP is only permitted for loopback testing)."

    let private base64Url (bytes: byte array) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private randomValue byteCount =
        RandomNumberGenerator.GetBytes(byteCount) |> base64Url

    let private createPkce () =
        let verifier = randomValue 48
        let challenge =
            verifier
            |> Encoding.ASCII.GetBytes
            |> SHA256.HashData
            |> base64Url

        verifier, challenge

    let private queryString (values: (string * string) list) =
        values
        |> List.map (fun (name, value) ->
            Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value))
        |> String.concat "&"

    let authorizationUri (config: OAuthConfig) state challenge =
        let query =
            [ "response_type", "code"
              "client_id", config.ClientId
              "redirect_uri", config.RedirectUri
              "audience", config.Audience
              "scope", scope
              "state", state
              "code_challenge", challenge
              "code_challenge_method", "S256" ]
            |> queryString

        Uri($"{issuer config}/authorize?{query}")

    let private tokenEndpoint (config: OAuthConfig) =
        $"{issuer config}/oauth/token"

    let private tryProperty (name: string) (root: JsonElement) =
        match root.TryGetProperty(name) with
        | true, value -> Some value
        | false, _ -> None

    let private oauthError (body: string) =
        try
            use doc = JsonDocument.Parse(body)
            match tryProperty "error" doc.RootElement with
            | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
            | _ -> null
        with
        | :? JsonException -> null
        | :? InvalidOperationException -> null

    let private parseTokenResponse (fallbackRefresh: string option) (response: HttpResponseMessage) (cancellationToken: CancellationToken) =
        task {
            let! body = response.Content.ReadAsStringAsync(cancellationToken)

            if not response.IsSuccessStatusCode then
                raise (OAuthTokenRequestFailure(response.StatusCode, oauthError body = "invalid_grant"))

            use document = JsonDocument.Parse(body)
            let root = document.RootElement

            let accessToken =
                match tryProperty "access_token" root with
                | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
                | _ -> null

            if String.IsNullOrWhiteSpace(accessToken) then
                invalidOp "OAuth token response did not contain an access token."

            let expiresIn =
                match tryProperty "expires_in" root with
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    match value.TryGetInt32() with
                    | true, seconds -> max 1 seconds
                    | false, _ -> 3600
                | _ -> 3600

            let refreshToken =
                match tryProperty "refresh_token" root with
                | Some value when value.ValueKind = JsonValueKind.String ->
                    match value.GetString() with
                    | null
                    | "" -> fallbackRefresh
                    | token -> Some token
                | _ -> fallbackRefresh

            return
                { AccessToken = accessToken
                  RefreshToken = refreshToken
                  ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(float expiresIn) }
        }

    let private postForm (client: HttpClient) (endpoint: string) (values: (string * string) list) (cancellationToken: CancellationToken) =
        task {
            use body =
                values
                |> Seq.map (fun (name, value) -> KeyValuePair<string, string>(name, value))
                |> fun pairs -> new FormUrlEncodedContent(pairs)
            return! client.PostAsync(endpoint, body, cancellationToken)
        }

    let private postToken (client: HttpClient) (config: OAuthConfig) values fallbackRefresh =
        task {
            use! response = postForm client (tokenEndpoint config) values CancellationToken.None
            return! parseTokenResponse fallbackRefresh response CancellationToken.None
        }

    type private DeviceAuthorization =
        { Prompt: DeviceAuthorizationPrompt
          DeviceCode: string
          Lifetime: TimeSpan
          Interval: TimeSpan }

    let private parseDeviceAuthorization (body: string) =
        try
            use doc = JsonDocument.Parse(body)
            let root = doc.RootElement
            let requiredString name =
                match tryProperty name root with
                | Some value when value.ValueKind = JsonValueKind.String ->
                    let text = value.GetString()
                    if String.IsNullOrWhiteSpace(text) then invalidOp "Incomplete device authorization response."
                    text
                | _ -> invalidOp "Incomplete device authorization response."
            let positiveSeconds name fallback =
                match tryProperty name root with
                | None -> fallback |> Option.defaultWith (fun () -> invalidOp "Incomplete device authorization response.")
                | Some value when value.ValueKind = JsonValueKind.Number ->
                    match value.TryGetInt32() with
                    | true, seconds when seconds > 0 -> seconds
                    | _ -> invalidOp "Invalid device authorization timing."
                | _ -> invalidOp "Invalid device authorization timing."
            let verificationUri =
                match Uri.TryCreate(requiredString "verification_uri", UriKind.Absolute) with
                | true, uri when (uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback))
                                 && String.IsNullOrEmpty(uri.UserInfo) -> uri
                | _ -> invalidOp "Invalid device verification URL."
            let userCode = requiredString "user_code"
            if userCode.Length > 128 || (userCode |> Seq.exists (fun c -> c < ' ' || c > '~')) then
                invalidOp "Invalid device user code."
            let expires = positiveSeconds "expires_in" None
            // CancellationTokenSource has a finite timer range. Reject nonsensical
            // long-lived enrollment grants instead of silently changing their expiry.
            if int64 expires * 1000L >= int64 UInt32.MaxValue then invalidOp "Invalid device authorization lifetime."
            { Prompt = { VerificationUri = verificationUri; UserCode = userCode }
              DeviceCode = requiredString "device_code"
              Lifetime = TimeSpan.FromSeconds(float expires)
              Interval = TimeSpan.FromSeconds(float (positiveSeconds "interval" (Some 5))) }
        with :? JsonException -> invalidOp "Malformed device authorization response."

    type private DevicePoll =
        | Authorized of OAuthTokenSet
        | AuthorizationPending
        | SlowDown
        | RetryNetwork

    let private pollDevice (client: HttpClient) (config: OAuthConfig) code (ct: CancellationToken) =
        task {
            try
                use! response =
                    postForm client (tokenEndpoint config)
                        [ "grant_type", "urn:ietf:params:oauth:grant-type:device_code"
                          "client_id", config.ClientId
                          "device_code", code ] ct
                if response.IsSuccessStatusCode then
                    let! tokens = parseTokenResponse None response ct
                    return Authorized tokens
                else
                    let! body = response.Content.ReadAsStringAsync(ct)
                    match oauthError body with
                    | "authorization_pending" -> return AuthorizationPending
                    | "slow_down" -> return SlowDown
                    | "access_denied" -> return invalidOp "Device authorization denied. Run enrollment again when ready."
                    | "expired_token" -> return invalidOp "Device authorization expired. Run enrollment again."
                    | _ when int response.StatusCode = 429 -> return SlowDown
                    | _ when int response.StatusCode >= 500 -> return RetryNetwork
                    | _ -> return invalidOp $"Device authorization token request rejected (HTTP {int response.StatusCode}). Check the Auth0 device grant configuration."
            with
            | :? HttpRequestException when not ct.IsCancellationRequested -> return RetryNetwork
            | :? OperationCanceledException when not ct.IsCancellationRequested -> return RetryNetwork
        }

    let private deviceLogin (client: HttpClient) (config: OAuthConfig) (clock: TimeProvider)
                            (delay: TimeSpan -> CancellationToken -> Task)
                            (present: DeviceAuthorizationPrompt -> unit) (ct: CancellationToken) =
        task {
            requireSecureIssuer config
            use! response =
                postForm client ($"{issuer config}/oauth/device/code")
                    [ "client_id", config.ClientId; "audience", config.Audience; "scope", scope ] ct
            if not response.IsSuccessStatusCode then
                invalidOp $"Device authorization request rejected (HTTP {int response.StatusCode}). Check the Auth0 application device grant."
            let! body = response.Content.ReadAsStringAsync(ct)
            let grant = parseDeviceAuthorization body
            let started = clock.GetTimestamp()
            use expiry = new CancellationTokenSource(grant.Lifetime, clock)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token)
            let remaining () = grant.Lifetime - clock.GetElapsedTime(started)
            let expired () = invalidOp "Device authorization expired. Run enrollment again."
            let rec loop (interval: TimeSpan) (backoff: TimeSpan) : Task<OAuthTokenSet> =
                task {
                    let wait = max interval backoff
                    let left = remaining()
                    if left <= TimeSpan.Zero then return expired()
                    else
                        do! delay (min wait left) linked.Token
                        linked.Token.ThrowIfCancellationRequested()
                        if remaining() <= TimeSpan.Zero then return expired()
                        else
                            let! result = pollDevice client config grant.DeviceCode linked.Token
                            linked.Token.ThrowIfCancellationRequested()
                            if remaining() <= TimeSpan.Zero then return expired()
                            else
                                match result with
                                | Authorized tokens -> return tokens
                                | AuthorizationPending -> return! loop interval TimeSpan.Zero
                                | SlowDown -> return! loop (interval + TimeSpan.FromSeconds(5.0)) TimeSpan.Zero
                                | RetryNetwork -> return! loop interval (min (remaining()) (wait + wait))
                }
            try
                ct.ThrowIfCancellationRequested()
                present grant.Prompt
                return! loop grant.Interval TimeSpan.Zero
            with :? OperationCanceledException when expiry.IsCancellationRequested && not ct.IsCancellationRequested ->
                return expired()
        }

    let private sendBrowserResponse (context: HttpListenerContext) success =
        task {
            let heading, detail =
                if success then
                    "Jarvis connected", "Authentication is complete. You can close this window."
                else
                    "Jarvis authentication failed", "Return to the terminal for details."

            let html =
                $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{heading}</title></head><body><h1>{heading}</h1><p>{detail}</p></body></html>"

            let bytes = Encoding.UTF8.GetBytes(html)
            context.Response.StatusCode <- if success then 200 else 400
            context.Response.ContentType <- "text/html; charset=utf-8"
            context.Response.ContentLength64 <- int64 bytes.Length
            do! context.Response.OutputStream.WriteAsync(bytes.AsMemory())
            context.Response.Close()
        }

    let isWslRelease isLinux (release: string) =
        isLinux && not (isNull release) && release.Contains("microsoft", StringComparison.OrdinalIgnoreCase)

    let private isWsl () =
        if not (OperatingSystem.IsLinux()) then false
        else
            try isWslRelease true (File.ReadAllText("/proc/sys/kernel/osrelease"))
            with _ -> false

    let private openBrowser (log: string -> unit) (uri: Uri) =
        try
            let startInfo = ProcessStartInfo(uri.AbsoluteUri)
            startInfo.UseShellExecute <- true
            Process.Start(startInfo) |> ignore
        with _ ->
            log "Could not open the browser automatically."

    let presentAuthorization isWsl (showLink: string option -> unit) (launch: Uri -> unit) (authorization: Uri) =
        showLink (Some authorization.AbsoluteUri)
        if not isWsl then launch authorization

    let callbackIsValid (expectedPath: string) (expectedState: string) (path: string) (state: string) (error: string) (code: string) =
        String.Equals(path, expectedPath, StringComparison.Ordinal)
        && String.Equals(state, expectedState, StringComparison.Ordinal)
        && String.IsNullOrWhiteSpace(error)
        && not (String.IsNullOrWhiteSpace(code))

    let private interactiveLogin config (log: string -> unit) (showLink: string option -> unit) =
        task {
            let verifier, challenge = createPkce ()
            let state = randomValue 32
            let authorization = authorizationUri config state challenge
            let redirect = Uri(config.RedirectUri)
            if not redirect.IsLoopback then
                invalidOp "OAuth redirect URI must use a loopback host."
            let listenerPrefix = $"{redirect.Scheme}://{redirect.Host}:{redirect.Port}/"

            use listener = new HttpListener()
            listener.Prefixes.Add(listenerPrefix)
            listener.Start()

            let! context =
                task {
                    try
                        presentAuthorization (isWsl ()) showLink (openBrowser log) authorization
                        return! listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5.0))
                    finally
                        showLink None
                }

            let query = context.Request.QueryString
            let returnedState = query["state"]
            let error = query["error"]
            let errorDescription = query["error_description"]
            let code = query["code"]

            let callbackIsValid =
                callbackIsValid redirect.AbsolutePath state context.Request.Url.AbsolutePath returnedState error code

            do! sendBrowserResponse context callbackIsValid

            if not (String.Equals(context.Request.Url.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal)) then
                invalidOp $"Unexpected OAuth callback path: {context.Request.Url.AbsolutePath}"

            if not (String.Equals(returnedState, state, StringComparison.Ordinal)) then
                invalidOp "OAuth state validation failed."

            if not (String.IsNullOrWhiteSpace(error)) then
                let description =
                    if String.IsNullOrWhiteSpace(errorDescription) then error else $"{error}: {errorDescription}"
                invalidOp $"OAuth authorization failed: {description}"

            if String.IsNullOrWhiteSpace(code) then
                invalidOp "OAuth authorization response did not contain a code."

            return!
                postToken
                    http
                    config
                    [ "grant_type", "authorization_code"
                      "client_id", config.ClientId
                      "code", code
                      "code_verifier", verifier
                      "redirect_uri", config.RedirectUri ]
                    None
        }

    let private refresh client config refreshToken =
        postToken
            client
            config
            [ "grant_type", "refresh_token"
              "client_id", config.ClientId
              "refresh_token", refreshToken ]
            (Some refreshToken)

    let private cachePath () =
        Path.Combine(Path.GetDirectoryName(ClientConfig.configPath()), "oauth-refresh.json")

    // Only a refresh token is retained, never the access token. Credentials
    // are separate from config.json so config exports cannot include secrets.
    let loadRefreshTokenAt (path: string) (config: OAuthConfig) =
        try
            if not (File.Exists path) then None
            elif File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then None
            elif (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                 && (File.GetUnixFileMode(path) &&& (UnixFileMode.GroupRead ||| UnixFileMode.GroupWrite ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherWrite ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute)) <> enum<UnixFileMode> 0 then None
            else
                use doc = JsonDocument.Parse(File.ReadAllText path)
                let root = doc.RootElement
                let prop (name: string) = root.GetProperty(name).GetString()
                if prop "issuer" = issuer config
                   && prop "audience" = config.Audience
                   && prop "clientId" = config.ClientId then
                    match prop "refreshToken" with
                    | null | "" -> None
                    | token -> Some token
                else None
        with _ -> None

    let saveRefreshTokenAt (path: string) (config: OAuthConfig) (token: string) =
        if String.IsNullOrWhiteSpace token then invalidArg (nameof token) "Missing refresh token."
        let directory = Path.GetDirectoryName(path)
        Directory.CreateDirectory(directory) |> ignore
        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            let payload =
                JsonSerializer.Serialize(
                    {| issuer = issuer config
                       audience = config.Audience
                       clientId = config.ClientId
                       refreshToken = token |})
            let options = FileStreamOptions()
            options.Mode <- FileMode.CreateNew
            options.Access <- FileAccess.Write
            options.Share <- FileShare.None
            if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                options.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
            do
                use stream = new FileStream(temporary, options)
                use writer = new StreamWriter(stream, new UTF8Encoding(false))
                writer.Write(payload)
                writer.Flush()
                stream.Flush(true)
            if File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) then
                invalidOp "OAuth credential path must not be a symbolic link."
            File.Move(temporary, path, true)
        finally
            if File.Exists temporary then File.Delete temporary

    // Explicit enrollment is a foreground operation. Only the verification URL
    // and user code are passed to its presenter, never to the activity logger.
    let enrollDeviceAt (client: HttpClient) (config: OAuthConfig) path (clock: TimeProvider)
                       delay present (ct: CancellationToken) =
        task {
            let! tokens = deviceLogin client config clock delay present ct
            ct.ThrowIfCancellationRequested()
            match tokens.RefreshToken with
            | Some token -> saveRefreshTokenAt path config token
            | None -> invalidOp "Device enrollment did not return a refresh token. Enable offline access and the refresh token grant in Auth0."
        }

    let enrollDevice present ct =
        enrollDeviceAt deviceHttp (configuration()) (cachePath()) TimeProvider.System
            (fun duration token -> Task.Delay(duration, token)) present ct

    let runDeviceEnrollmentCommand () =
        if Console.IsInputRedirected || Console.IsOutputRedirected then
            eprintfn "Device enrollment requires a foreground terminal (for SSH, use ssh -t)."
            2
        else
            use cancellation = new CancellationTokenSource()
            let handler = ConsoleCancelEventHandler(fun _ args ->
                args.Cancel <- true
                cancellation.Cancel())
            Console.CancelKeyPress.AddHandler handler
            try
                try
                    let present (prompt: DeviceAuthorizationPrompt) =
                        printfn "On another device, open %s" prompt.VerificationUri.AbsoluteUri
                        printfn "Enter code: %s" prompt.UserCode
                        printfn "Waiting for authorization. Press Ctrl+C to cancel."
                    (enrollDevice present cancellation.Token).GetAwaiter().GetResult()
                    printfn "Device enrollment complete. Refresh credentials saved."
                    0
                with
                | :? OperationCanceledException when cancellation.IsCancellationRequested ->
                    eprintfn "Device enrollment cancelled."
                    130
                | :? InvalidOperationException as error ->
                    eprintfn "%s" error.Message
                    1
                | :? HttpRequestException ->
                    eprintfn "Could not contact Auth0. Check network connectivity and configuration."
                    1
                | _ ->
                    eprintfn "Device enrollment failed. Check Auth0 response and credential storage permissions."
                    1
            finally
                Console.CancelKeyPress.RemoveHandler handler

    let private reauthorizationRequired (log: string -> unit) =
        let message = "OAuth reauthorization required. Stop the service and run foreground device enrollment as the service user before restarting."
        log message
        invalidOp message

    let private refreshUnavailable (log: string -> unit) =
        let message = "OAuth refresh failed. Check network connectivity and Auth0 application settings; retry without interactive sign-in."
        log message
        invalidOp message

    let private loginAt (client: HttpClient) config path unattended (log: string -> unit) (showLink: string option -> unit) =
        task {
            let remember (tokens: OAuthTokenSet) =
                match tokens.RefreshToken with
                | Some token ->
                    try saveRefreshTokenAt path config token
                    with _ ->
                        if unattended then
                            let message = "Could not persist OAuth refresh credentials. Check service-user credential storage permissions."
                            log message
                            invalidOp message
                        else log "Could not persist OAuth refresh credentials; a later restart may require sign-in."
                | None -> ()
            let! initial =
                task {
                    match loadRefreshTokenAt path config with
                    | Some token ->
                        try
                            let! result = refresh client config token
                            log "Jarvis OAuth session restored without browser sign-in."
                            return result
                        with
                        | :? OAuthTokenRequestFailure as error when unattended && error.ReauthorizationRequired ->
                            return reauthorizationRequired log
                        | _ when not unattended ->
                            log "Stored OAuth session could not be restored; interactive sign-in required."
                            return! interactiveLogin config log showLink
                        | _ -> return refreshUnavailable log
                    | None when unattended -> return reauthorizationRequired log
                    | None -> return! interactiveLogin config log showLink
                }
            remember initial
            let gate = new SemaphoreSlim(1, 1)
            let mutable current = initial

            let getAccessToken () =
                task {
                    if current.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1.0) then
                        return current.AccessToken
                    else
                        do! gate.WaitAsync()

                        try
                            if current.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1.0) then
                                match current.RefreshToken with
                                | Some refreshToken ->
                                    let! refreshed =
                                        task {
                                            try return! refresh client config refreshToken
                                            with
                                            | :? OAuthTokenRequestFailure as error when unattended && error.ReauthorizationRequired ->
                                                return reauthorizationRequired log
                                            | _ when not unattended ->
                                                log "OAuth refresh failed; signing in again."
                                                return! interactiveLogin config log showLink
                                            | _ -> return refreshUnavailable log
                                        }
                                    remember refreshed
                                    current <- refreshed
                                | None when unattended -> reauthorizationRequired log
                                | None ->
                                    let! authenticated = interactiveLogin config log showLink
                                    current <- authenticated
                                    remember authenticated

                            return current.AccessToken
                        finally
                            gate.Release() |> ignore
                }

            return OAuthSession(getAccessToken)
        }

    let loginUnattendedAt client config path log =
        requireSecureIssuer config
        loginAt client config path true log (fun _ -> ())

    let login (log: string -> unit) (showLink: string option -> unit) =
        let config = configuration()
        let path = cachePath()
        match Environment.GetEnvironmentVariable("JARVIS_OAUTH_MODE") with
        | null | "" | "browser" -> loginAt http config path false log showLink
        | "unattended" ->
            let serviceLog message =
                if Console.IsOutputRedirected then eprintfn "%s" message
                log message
            loginUnattendedAt deviceHttp config path serviceLog
        | _ -> invalidOp "Unknown JARVIS_OAUTH_MODE. Use browser or unattended."
