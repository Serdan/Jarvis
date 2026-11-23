module Client.BrowserArguments

open System.Text
open System.Text.Encodings.Web
open System.Text.Json

// The bridge accepts JSON text, not HTML. Use this exact encoder for the
// minimal launch envelope; additional fields must be measured after composition.
[<Literal>]
let MaxBytes = 65536

let private utf8 = UTF8Encoding(false, true)
let private launchOptions = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let serializeMinimalLaunchPrompt (prompt: string) =
    JsonSerializer.Serialize({| prompt = prompt |}, launchOptions)

let byteCount (argsJson: string) = utf8.GetByteCount argsJson

let fits (argsJson: string) = byteCount argsJson <= MaxBytes
