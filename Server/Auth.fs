namespace Server

open System
open System.Security.Claims

module Auth =
    [<Literal>]
    let WorkspaceRead = "workspace:read"

    [<Literal>]
    let WorkspaceWrite = "workspace:write"

    [<Literal>]
    let ProcessExecute = "process:execute"

    [<Literal>]
    let GitWrite = "git:write"

    [<Literal>]
    let ClientConnect = "client:connect"

    [<Literal>]
    let ClientConnectPolicy = "jarvis-client-connect"

    let mcpScopes =
        [ WorkspaceRead
          WorkspaceWrite
          ProcessExecute
          GitWrite ]

    let allScopes = mcpScopes @ [ ClientConnect ]

    let issuer (options: JarvisOptions) =
        let domain = options.Auth0Domain.Trim().TrimEnd('/')
        if domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
            domain + "/"
        else
            "https://" + domain + "/"

    let resourceMetadataUri (options: JarvisOptions) =
        options.Audience.Trim().TrimEnd('/') + "/.well-known/oauth-protected-resource"

    let hasScope scope (principal: ClaimsPrincipal) =
        principal.Claims
        |> Seq.filter (fun claim -> claim.Type = "scope")
        |> Seq.collect (fun claim ->
            claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            :> seq<string>)
        |> Seq.exists (fun value -> String.Equals(value, scope, StringComparison.Ordinal))

    let tryUserId (principal: ClaimsPrincipal) =
        match principal.FindFirst("sub") with
        | null -> None
        | claim when String.IsNullOrWhiteSpace(claim.Value) -> None
        | claim -> Some claim.Value
