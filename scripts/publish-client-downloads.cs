#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Security.Cryptography;

const string ServerUrl = "https://jarvis2.kehlet.dev/client";

string[] rids = ["linux-x64", "win-x64", "osx-arm64"];

var root = FindRepositoryRoot();
var downloads = Path.Combine(root, "artifacts", "downloads");

Directory.CreateDirectory(downloads);

foreach (var rid in rids)
{
    var code = DotNet(
        "scripts/build.cs",
        "publish-client",
        "--rid", rid,
        "--server", ServerUrl);

    if (code != 0)
        return code;

    var sourceName = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? "JarvisClient.exe" : "JarvisClient";
    var targetName = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
        ? $"JarvisClient-{rid}.exe"
        : $"JarvisClient-{rid}";

    var source = Path.Combine(root, "artifacts", "client", rid, sourceName);
    var target = Path.Combine(downloads, targetName);

    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"Expected published client was not found: {source}");
        return 1;
    }

    File.Copy(source, target, overwrite: true);

    if (!rid.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
}

WriteChecksums(downloads);

Console.WriteLine();
Console.WriteLine("Client downloads are ready:");
foreach (var file in Directory.GetFiles(downloads).OrderBy(Path.GetFileName))
    Console.WriteLine($"  {Path.GetRelativePath(root, file)}");

return 0;

void WriteChecksums(string directory)
{
    var lines = Directory.GetFiles(directory)
        .Where(path => Path.GetFileName(path) != "SHA256SUMS")
        .OrderBy(Path.GetFileName)
        .Select(path => $"{Sha256(path)}  {Path.GetFileName(path)}")
        .ToArray();

    File.WriteAllLines(Path.Combine(directory, "SHA256SUMS"), lines);
}

string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    var hash = SHA256.HashData(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

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
