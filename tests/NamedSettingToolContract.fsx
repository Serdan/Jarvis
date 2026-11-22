#r "../Common/bin/Release/net11.0/Common.dll"
#r "../Server/bin/Release/net11.0/JarvisServer.dll"
open System
open System.Text.Json
open Common
open Server

let failures = ResizeArray<string>()
let check label condition =
    if not condition then failures.Add(label)
let json (text:string) =
    use document = JsonDocument.Parse(text)
    document.RootElement.Clone()
let pinnedRevision = String.replicate 64 "a"
let valid =
    [ "machine-name", "\"device-b\"", NamedClientSetting.MachineName
      "workspace-path", "\"/tmp/workspace-b\"", NamedClientSetting.WorkspacePath
      "trust-level", "\"none\"", NamedClientSetting.TrustLevel
      "allowed-environment-variables", "[\"SAFE_VAR\"]", NamedClientSetting.AllowedEnvironmentVariables
      "max-import-bytes", "65536", NamedClientSetting.MaxImportBytes
      "browser-port", "8400", NamedClientSetting.BrowserPort
      "native-health-enabled", "true", NamedClientSetting.NativeHealthEnabled
      "native-health-timeout-seconds", "60", NamedClientSetting.NativeHealthTimeoutSeconds ]
for name, payload, expected in valid do
    match McpToolHelpers.parseNamedSetting name "set" (json payload) pinnedRevision with
    | Ok cmd -> check ("typed setting "+name) (cmd.Setting = expected && cmd.Value.IsSome)
    | Error error -> failures.Add("valid "+name+" rejected: "+error)
    match McpToolHelpers.parseNamedSetting name "unset" (json "null") pinnedRevision with
    | Ok cmd -> check ("unset "+name) (cmd.Setting=expected && cmd.Value.IsNone)
    | Error error -> failures.Add("unset "+name+" rejected: "+error)
let rejected =
    [ "MachineName", "set", "\"other\""
      "workspace_path", "set", "\"/tmp\""
      "machine-name", "set", "123"
      "workspace-path", "set", "null"
      "trust-level", "set", "true"
      "allowed-environment-variables", "set", "\"SAFE_VAR\""
      "allowed-environment-variables", "set", "[1]"
      "max-import-bytes", "set", "2.5"
      "browser-port", "set", "\"8400\""
      "native-health-enabled", "set", "\"true\""
      "native-health-timeout-seconds", "set", "true"
      "machine-name", "unset", "\"name\""
      "machine-name", "SET", "\"new-name\""
      "machine-name", "set", "null" ]
for name, operation, value in rejected do
    check ("must reject "+name+"/"+operation+"/"+value)
        (McpToolHelpers.parseNamedSetting name operation (json value) pinnedRevision |> Result.isError)
check "invalid revision"
    (McpToolHelpers.parseNamedSetting "machine-name" "set" (json "\"good\"") "bad" |> Result.isError)
check "omitted revision rejected"
    (McpToolHelpers.parseNamedSetting "machine-name" "set" (json "\"good\"") "" |> Result.isError)
check "valid SHA-256 revision"
    (match McpToolHelpers.parseNamedSetting "machine-name" "set" (json "\"good\"") (String.replicate 64 "a") with
     | Ok cmd -> cmd.ExpectedRevision = Some(String.replicate 64 "a")
     | _ -> false)
let oversized = "\"" + String.replicate 4097 "a" + "\""
check "oversize value"
    (McpToolHelpers.parseNamedSetting "machine-name" "set" (json oversized) pinnedRevision |> Result.isError)
if failures.Count > 0 then
    for failure in failures do eprintfn "FAIL %s" failure
    failwithf "%d named-setting contract failures" failures.Count
printfn "NamedSettingToolContract: %d canonical set/unset positives, %d malformed negatives, revision + bound checks PASS" (valid.Length*2) rejected.Length
