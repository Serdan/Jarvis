module Client.ProcessEnvironment

open System
open System.Diagnostics

let private normalizeName (name: string) =
    name.Trim().ToUpperInvariant()

let private sensitiveNames =
    set
        [ "SSH_AUTH_SOCK"
          "SSH_AGENT_PID"
          "GPG_AGENT_INFO"
          "KUBECONFIG"
          "DOCKER_CONFIG"
          "GOOGLE_APPLICATION_CREDENTIALS"
          "AZURE_CONFIG_DIR"
          "NETRC"
          "GIT_ASKPASS"
          "SSH_ASKPASS"
          "DATABASE_URL"
          "REDIS_URL" ]

let private sensitiveFragments =
    [ "TOKEN"
      "SECRET"
      "PASSWORD"
      "PASSWD"
      "API_KEY"
      "APIKEY"
      "ACCESS_KEY"
      "PRIVATE_KEY"
      "CREDENTIAL"
      "AUTHORIZATION"
      "CONNECTION_STRING"
      "JWT"
      "_DSN" ]

let isSensitiveName (name: string) =
    let normalized = normalizeName name

    sensitiveNames.Contains normalized
    || (sensitiveFragments
        |> List.exists (fun fragment -> normalized.Contains(fragment, StringComparison.Ordinal)))

let private syncRoot = obj()
let mutable private allowedNames = Set.empty<string>

let configureAllowedEnvironmentVariables names =
    let configured =
        names
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.map normalizeName
        |> Set.ofSeq

    lock syncRoot (fun () -> allowedNames <- configured)

let private getAllowedNames () =
    lock syncRoot (fun () -> allowedNames)

let applyWithAllowed allowed (startInfo: ProcessStartInfo) =
    let allowed =
        allowed
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.map normalizeName
        |> Set.ofSeq

    let keys = startInfo.Environment.Keys |> Seq.toArray

    for key in keys do
        let normalized = normalizeName key

        if isSensitiveName key && not (allowed.Contains normalized) then
            startInfo.Environment.Remove key |> ignore

let apply (startInfo: ProcessStartInfo) =
    applyWithAllowed (getAllowedNames ()) startInfo
