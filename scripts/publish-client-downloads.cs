#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Security.Cryptography;

const string ServerUrl = "https://jarvis2.kehlet.dev/client";

// Keep these names aligned with Client/Modules/SelfUpdate.fs. Native Windows
// clients are retired; Windows Chrome -> WSL localhost is a separate bridge.
string[] rids = ["linux-x64", "osx-arm64"];

var root = FindRepositoryRoot();
var artifacts = Path.Combine(root, "artifacts");
var downloads = Path.Combine(artifacts, "downloads");
var stageRoot = Path.Combine(artifacts, $".downloads-stage-{Guid.NewGuid():N}");
var stageBuild = Path.Combine(stageRoot, "published");
var stageDownloads = Path.Combine(stageRoot, "downloads");
var backup = Path.Combine(artifacts, $".downloads-backup-{Guid.NewGuid():N}");

try
{
    // Never discard unknown files or follow a symlinked release directory.
    ValidateExistingDownloads(downloads);
    Directory.CreateDirectory(stageDownloads);

    foreach (var rid in rids)
    {
        var output = Path.Combine(stageBuild, rid);
        var code = DotNet(
            "scripts/build.cs", "publish-client",
            "--rid", rid,
            "--server", ServerUrl,
            "--configuration", "Release",
            "--output", output);

        if (code != 0)
            throw new InvalidOperationException($"Client publish failed for {rid} (exit {code}); previous downloads were preserved.");

        var source = Path.Combine(output, "JarvisClient");
        var target = Path.Combine(stageDownloads, $"JarvisClient-{rid}");

        if (!File.Exists(source) || new FileInfo(source).Length == 0)
            throw new InvalidDataException($"Expected nonempty published client was not found: {source}");

        File.Copy(source, target);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    WriteChecksums(stageDownloads, rids);

    // The public directory changes only after every artifact and checksum exists.
    // Replacing the whole directory eliminates stale Windows downloads and hashes.
    var previousMoved = false;
    if (Directory.Exists(downloads))
    {
        Directory.Move(downloads, backup);
        previousMoved = true;
    }

    try
    {
        Directory.Move(stageDownloads, downloads);
    }
    catch
    {
        if (previousMoved)
            Directory.Move(backup, downloads);
        throw;
    }

    // A failed cleanup is not a failed publish; retain backup for manual cleanup.
    if (previousMoved)
    {
        try { Directory.Delete(backup, recursive: true); }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: old release backup remains at {backup}: {ex.Message}"); }
    }

    Console.WriteLine();
    Console.WriteLine("Client downloads are ready:");
    foreach (var file in Directory.GetFiles(downloads).OrderBy(Path.GetFileName))
        Console.WriteLine($"  {Path.GetRelativePath(root, file)}");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Client downloads were not published: {ex.Message}");
    return 1;
}
finally
{
    if (Directory.Exists(stageRoot))
    {
        try { Directory.Delete(stageRoot, recursive: true); }
        catch (Exception ex) { Console.Error.WriteLine($"Warning: staging cleanup failed at {stageRoot}: {ex.Message}"); }
    }
}

void ValidateExistingDownloads(string directory)
{
    if (File.Exists(directory))
        throw new InvalidDataException($"Download destination is a file: {directory}");

    if (!Directory.Exists(directory))
        return;

    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException($"Refusing symlinked download directory: {directory}");

    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
    {
        var name = Path.GetFileName(entry);
        if (!File.Exists(entry) ||
            (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
            (name != "SHA256SUMS" && !name.StartsWith("JarvisClient-", StringComparison.Ordinal)))
            throw new InvalidDataException($"Refusing to replace unmanaged or linked download entry: {entry}");
    }
}

void WriteChecksums(string directory, IReadOnlyList<string> runtimeIds)
{
    var names = runtimeIds.Select(rid => $"JarvisClient-{rid}").ToArray();
    var lines = names.Order(StringComparer.Ordinal)
        .Select(name => $"{Sha256(Path.Combine(directory, name))}  {name}")
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
