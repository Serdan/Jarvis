module Client.ProviderMcpProvisioning

open System
open System.Text.RegularExpressions
open Client.ProviderLaunch

/// Desired profile is trusted host configuration, not model-supplied authority.
/// Tool names are exact provider-visible MCP names, not display titles.
type Scope =
    { Id: string
      ConfigurationIdentity: string
      Endpoint: Uri
      SurfaceName: string
      Tools: Set<string> }

type Context =
    | Browser of projectContextId: string * windowRegistration: string * url: string * startUrl: string option
    | Codex of executable: string * workingDirectory: string

type Surface =
    { Name: string
      Endpoint: Uri
      Tools: Set<string> }

/// Produced only by the existing authenticated host/endpoint policy reader.
/// The ceiling is the complete set callable through that authenticated route,
/// not a filtered tools/list response or the client's requested allowlist.
type ServerPolicy =
    { Identity: LaunchIdentity
      ProfileId: string
      McpProfile: string
      ConfigurationIdentity: string
      Endpoint: Uri
      CallableTools: Set<string> }

/// Produced by the trusted provider-context reader for the exact invocation.
/// None means unknown/incomplete (including inherited/plugin MCP sources).
/// Neither this record nor validation authenticates its producers.
type Evidence =
    { Identity: LaunchIdentity
      ProfileId: string
      McpProfile: string
      ConfigurationIdentity: string
      Context: Context
      EffectiveSurfaces: Surface list option
      Policy: ServerPolicy option }

let private nonblank (s: string) = not (String.IsNullOrWhiteSpace s)

let private safeEndpoint (uri: Uri) =
    not (isNull uri) && uri.IsAbsoluteUri && uri.UserInfo = ""
    && uri.Query = "" && uri.Fragment = ""
    && (uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback))

let private toolName (name: string) =
    nonblank name && Regex.IsMatch(name, "\\A[A-Za-z0-9_.-]{1,128}\\z")

let private exactEndpoint (expected: Uri) (actual: Uri) =
    safeEndpoint actual && String.Equals(expected.AbsoluteUri, actual.AbsoluteUri, StringComparison.Ordinal)

/// Exact equality is deliberate: a missing tool can invalidate the task;
/// additional tools or servers violate focus even if the intended tool works.
/// No credentials, grants or authorization decisions are created here.
let validate (request: LaunchRequest) context (scope: Scope) evidence =
    match validateRequest request with
    | Error reason -> Error reason
    | Ok () when not (nonblank scope.Id) || scope.Id <> request.McpProfile
                 || not (nonblank scope.ConfigurationIdentity)
                 || not (safeEndpoint scope.Endpoint) || not (nonblank scope.SurfaceName)
                 || Set.isEmpty scope.Tools || not (Set.forall toolName scope.Tools) ->
        Error "Invalid MCP profile definition"
    | Ok () ->
        match evidence with
        | None -> Error "Effective MCP provisioning evidence unavailable"
        | Some (e: Evidence) when e.Identity <> request.Identity
                                  || e.ProfileId <> request.Profile.Id
                                  || e.McpProfile <> scope.Id
                                  || e.ConfigurationIdentity <> scope.ConfigurationIdentity || e.Context <> context ->
            Error "MCP provisioning evidence belongs to another launch or context"
        | Some e ->
            match e.EffectiveSurfaces, e.Policy with
            | Some [surface], Some policy
                when surface.Name = scope.SurfaceName
                     && exactEndpoint scope.Endpoint surface.Endpoint
                     && surface.Tools = scope.Tools
                     && policy.Identity = request.Identity
                     && policy.ProfileId = request.Profile.Id
                     && policy.McpProfile = scope.Id
                     && policy.ConfigurationIdentity = scope.ConfigurationIdentity
                     && exactEndpoint scope.Endpoint policy.Endpoint
                     && policy.CallableTools = scope.Tools -> Ok ()
            | _ -> Error "Effective MCP surface or authenticated server ceiling does not match profile"
