#!/usr/bin/env dotnet
// Explicit operator action: build the Linux server and collect its verified download.
using System.Diagnostics;

var root = FindRepositoryRoot();
var preflight = DotNet("scripts/release-provenance.cs", "preflight");
if (preflight != 0) return preflight;

var build = DotNet("scripts/build.cs", "publish-server", "--rid", "linux-x64");
if (build != 0) return build;

var collect = DotNet("scripts/release-provenance.cs", "collect",
    Path.Combine("artifacts", "server", "linux-x64", "JarvisServer"), "JarvisServer-linux-x64");
if (collect != 0) return collect;

var verify = DotNet("scripts/release-provenance.cs", "verify");
if (verify != 0) return verify;
Console.WriteLine("Linux server download has verified checksums and release provenance.");
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
