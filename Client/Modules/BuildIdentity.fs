module Client.BuildIdentity

open System
open System.Reflection
open Common

type Identity =
    { ApplicationVersion: string
      GitCommit: string
      RuntimeIdentifier: string
      Dirty: bool
      ProtocolVersion: string }

let private assemblyVersion () =
    let assembly = Assembly.GetExecutingAssembly()
    match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
    | null ->
        match assembly.GetName().Version with
        | null -> "unknown"
        | version -> version.ToString()
    | attribute ->
        let value = attribute.InformationalVersion.Split('+')[0]
        if String.IsNullOrWhiteSpace value then "unknown" else value

let current () =
    { ApplicationVersion = assemblyVersion ()
      GitCommit = BuildInfo.GitCommit
      RuntimeIdentifier = BuildInfo.BuildRid
      Dirty = BuildInfo.BuildDirty
      ProtocolVersion = AgentProtocol.version }

let private validCommit (commit: string) =
    (commit.Length = 40 || commit.Length = 64) && Seq.forall Uri.IsHexDigit commit

let private commitText (identity: Identity) =
    if validCommit identity.GitCommit then identity.GitCommit.ToLowerInvariant()
    else "unknown"

let shortCommit (identity: Identity) =
    let commit = commitText identity
    if commit.Length > 12 then commit.Substring(0, 12) else commit

let buildLabel (identity: Identity) =
    let state = if identity.Dirty || not (validCommit identity.GitCommit) then "dirty/development" else "clean"
    sprintf "%s+%s (%s; %s)" identity.ApplicationVersion (commitText identity) identity.RuntimeIdentifier state

let registrationVersion () = buildLabel (current ())

let detail (identity: Identity) =
    sprintf "Jarvis Client %s\nGit commit: %s\nProtocol: %s\nRID: %s\nBuild: %s"
        identity.ApplicationVersion (commitText identity) identity.ProtocolVersion identity.RuntimeIdentifier
        (if identity.Dirty || not (validCommit identity.GitCommit) then "dirty/development" else "clean")

let compact (identity: Identity) =
    sprintf "v%s P%s %s%s"
        identity.ApplicationVersion identity.ProtocolVersion (shortCommit identity)
        (if identity.Dirty || not (validCommit identity.GitCommit) then " dirty" else "")
