#!/usr/bin/env dotnet

using System.Diagnostics;

const string Rid = "linux-x64";
const string ServerUrl = "https://jarvis2.kehlet.dev/client";

var root = FindRepositoryRoot();

var serverCode = DotNet(
    "scripts/build.cs",
    "publish-server",
    "--rid", Rid);

if (serverCode != 0)
    return serverCode;

var clientCode = DotNet(
    "scripts/build.cs",
    "publish-client",
    "--rid", Rid,
    "--server", ServerUrl);

if (clientCode != 0)
    return clientCode;

Console.WriteLine();
Console.WriteLine("Published Jarvis 2 linux artifacts:");
Console.WriteLine($"  Server: artifacts/server/{Rid}/JarvisServer");
Console.WriteLine($"  Client: artifacts/client/{Rid}/JarvisClient");

return 0;

int DotNet(params string[] arguments)
{
    Console.WriteLine();
    Console.WriteLine("dotnet " + string.Join(' ', arguments.Select(QuoteIfNeeded)));

    using var process = new Process();
    process.StartInfo.FileName = "dotnet";
    process.StartInfo.WorkingDirectory = root;
    process.StartInfo.UseShellExecute = false;

    foreach (var argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);

    process.Start();
    process.WaitForExit();
    return process.ExitCode;
}

string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());

    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Jarvis.slnx")))
            return current.FullName;

        current = current.Parent;
    }

    current = new DirectoryInfo(AppContext.BaseDirectory);

    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Jarvis.slnx")))
            return current.FullName;

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not find repository root containing Jarvis.slnx.");
}

string QuoteIfNeeded(string value)
{
    if (value.Any(char.IsWhiteSpace))
        return '"' + value.Replace("\"", "\\\"") + '"';

    return value;
}
