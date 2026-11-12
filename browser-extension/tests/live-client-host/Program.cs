// Test-only process hosting the exact JarvisClient BrowserBridge.Service assembly.
// No OAuth, no client TUI and no production config/browser profile modifications.
using System.Text;
using System.Text.Json;
using Client;
using Microsoft.FSharp.Core;

if (args.Length != 1 || !int.TryParse(args[0], out var port) || port < 1024 || port > 65535)
{
    Console.Error.WriteLine("Expected one loopback bridge port argument.");
    return 2;
}
var secret = Environment.GetEnvironmentVariable("JARVIS_TEST_BRIDGE_SECRET");
if (secret is null || secret.Length != 64 || !secret.All(char.IsAsciiHexDigit))
{
    Console.Error.WriteLine("Missing temporary bridge secret.");
    return 2;
}

using var service = new BrowserBridge.Service(port, secret);
service.Start();
Console.WriteLine(JsonSerializer.Serialize(new { kind = "ready", port }));
Console.Out.Flush();

while (Console.ReadLine() is { } line)
{
    string id = "";
    try
    {
        using var input = JsonDocument.Parse(line);
        var payload = input.RootElement;
        id = payload.GetProperty("id").GetString() ?? "";
        var kind = payload.GetProperty("kind").GetString() ?? "";
        string Str(string name, string fallback = "") =>
            payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback : fallback;
        int Num(string name, int fallback = 30000) =>
            payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetInt32() : fallback;

        FSharpResult<string, string> result = kind switch
        {
            "windows" => service.ListWindows(),
            "tabs" => service.ListTabs(),
            "openTab" => await service.OpenTab(Str("windowRegistration"), Str("url"), Str("alias"),
                payload.TryGetProperty("active", out var active) && active.GetBoolean()),
            "executeJs" => await service.Execute(Str("tabRegistration"), Str("source"), Str("argsJson", "{}"),
                Num("timeoutMs"), Str("entrypoint", "execute") == "expression"),
            "captureDom" => await service.CaptureDom(Str("tabRegistration"),
                Str("waitJson", "{\"kind\":\"immediate\"}"), Str("captureSelector"), Num("timeoutMs"),
                Str("outputPath") is { Length: > 0 } target ? FSharpOption<string>.Some(target) : null),
            "closeTab" => await service.CloseTab(Str("tabRegistration")),
            _ => throw new ArgumentException("Unsupported test operation.")
        };
        var output = result.IsOk
            ? JsonSerializer.Serialize(new { id, ok = true,
                data = JsonDocument.Parse(result.ResultValue).RootElement.Clone() })
            : JsonSerializer.Serialize(new { id, ok = false, error = result.ErrorValue });
        Console.WriteLine(output);
    }
    catch (Exception ex)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            id, ok = false, error = "TestHostError:" + ex.GetType().Name
        }));
    }
    Console.Out.Flush();
}
return 0;
