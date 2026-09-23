namespace Client

open System
open System.Collections.Generic
open System.Diagnostics
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
              "scope", "openid profile offline_access client:connect"
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

    let private parseTokenResponse (fallbackRefresh: string option) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()

            if not response.IsSuccessStatusCode then
                invalidOp $"OAuth token request failed with HTTP {int response.StatusCode}."

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

    let private postToken (config: OAuthConfig) (values: (string * string) list) (fallbackRefresh: string option) =
        task {
            use body =
                values
                |> Seq.map (fun (name, value) -> KeyValuePair<string, string>(name, value))
                |> fun pairs -> new FormUrlEncodedContent(pairs)

            use! response = http.PostAsync(tokenEndpoint config, body)
            return! parseTokenResponse fallbackRefresh response
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

    let private openBrowser (log: string -> unit) (uri: Uri) =
        try
            let startInfo = ProcessStartInfo(uri.AbsoluteUri)
            startInfo.UseShellExecute <- true
            Process.Start(startInfo) |> ignore
        with ex ->
            log $"Could not open the browser automatically: {ex.Message}"
            log $"Open this URL to sign in: {uri.AbsoluteUri}"

    let private interactiveLogin config (log: string -> unit) =
        task {
            let verifier, challenge = createPkce ()
            let state = randomValue 32
            let authorization = authorizationUri config state challenge
            let redirect = Uri(config.RedirectUri)
            let listenerPrefix = $"{redirect.Scheme}://{redirect.Host}:{redirect.Port}/"

            use listener = new HttpListener()
            listener.Prefixes.Add(listenerPrefix)
            listener.Start()

            log "Opening browser for Jarvis sign-in..."
            openBrowser log authorization

            let! context =
                listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5.0))

            let query = context.Request.QueryString
            let returnedState = query["state"]
            let error = query["error"]
            let errorDescription = query["error_description"]
            let code = query["code"]

            let callbackIsValid =
                String.Equals(context.Request.Url.AbsolutePath, redirect.AbsolutePath, StringComparison.Ordinal)
                && String.Equals(returnedState, state, StringComparison.Ordinal)
                && String.IsNullOrWhiteSpace(error)
                && not (String.IsNullOrWhiteSpace(code))

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
                    config
                    [ "grant_type", "authorization_code"
                      "client_id", config.ClientId
                      "code", code
                      "code_verifier", verifier
                      "redirect_uri", config.RedirectUri ]
                    None
        }

    let private refresh config refreshToken =
        postToken
            config
            [ "grant_type", "refresh_token"
              "client_id", config.ClientId
              "refresh_token", refreshToken ]
            (Some refreshToken)

    let login (log: string -> unit) =
        task {
            let config = configuration ()
            let! initial = interactiveLogin config log
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
                                    try
                                        let! refreshed = refresh config refreshToken
                                        current <- refreshed
                                    with ex ->
                                        log $"OAuth refresh failed; signing in again: {ex.Message}"
                                        let! authenticated = interactiveLogin config log
                                        current <- authenticated
                                | None ->
                                    let! authenticated = interactiveLogin config log
                                    current <- authenticated

                            return current.AccessToken
                        finally
                            gate.Release() |> ignore
                }

            return OAuthSession(getAccessToken)
        }
