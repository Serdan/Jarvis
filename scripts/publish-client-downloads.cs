#!/usr/bin/env dotnet
// Explicit operator action: build all client RIDs and collect verified download files.
using System.Diagnostics;

const string ServerUrl = "https://jarvis2.kehlet.dev/client";
string[] rids = ["linux-x64", "win-x64", "osx-arm64"];
var root = FindRepositoryRoot();

var preflight = DotNet("scripts/release-provenance.cs", "preflight");
if (preflight != 0) return preflight;

foreach (var rid in rids)
{
    var build = DotNet("scripts/build.cs", "publish-client", "--rid", rid, "--server", ServerUrl);
    if (build != 0) return build;

    var isWindows = rid.StartsWith("win-", StringComparison.Ordinal);
    var file = isWindows ? "JarvisClient.exe" : "JarvisClient";
    var source = Path.Combine("artifacts", "client", rid, file);
    var target = isWindows ? $"JarvisClient-{rid}.exe" : $"JarvisClient-{rid}";
    var collect = DotNet("scripts/release-provenance.cs", "collect", source, target);
    if (collect != 0) return collect;
}

var verify = DotNet("scripts/release-provenance.cs", "verify");
if (verify != 0) return verify;
Console.WriteLine("Client downloads have verified checksums and release provenance in artifacts/downloads.");
return 0;

int DotNet(params string[] arguments)
{
    using var process = new Process();
    process.StartInfo.FileName = "dotnet";
    process.StartInfo.WorkingDirectory = root;
    process.StartInfo.UseShellExecute = false;
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    Console.WriteLine("dotnet " + string.Join(' ', arguments));
    process.Start();
    process.WaitForExit();
    return process.ExitCode;
}

string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Jarvis.slnx"))) return current.FullName;
        current = current.Parent;
    }
    throw new InvalidOperationException("Could not find Jarvis.slnx.");
}
