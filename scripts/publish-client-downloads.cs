#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

const string ServerUrl = "https://jarvis2.kehlet.dev/client";

string[] rids = ["linux-x64", "win-x64", "osx-arm64"];

var root = FindRepositoryRoot();
var downloads = Path.Combine(root, "artifacts", "downloads");

Directory.CreateDirectory(downloads);

// The project file is the single application-version authority. The full SHA
// distinguishes builds without requiring a semantic-version bump per commit.
var version = XDocument.Load(Path.Combine(root, "Client", "Client.fsproj"))
    .Descendants("Version").First().Value.Trim();
var commit = Git("rev-parse", "HEAD").Trim().ToLowerInvariant();
if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
    throw new InvalidOperationException("Unable to establish a full Git source identity.");
var dirty = Git("status", "--porcelain", "--untracked-files=normal").Length != 0;
var releaseId = version + "-" + commit[..12] + (dirty ? "-dirty" : "");
var immutableDirectory = Path.Combine(downloads, "releases", releaseId);
Directory.CreateDirectory(immutableDirectory);
var artifacts = new List<(string Rid, string FileName, string ReleasePath, string Sha256, long Bytes)>();

foreach (var rid in rids)
{
    var isWin = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
    var code = DotNet(
        "scripts/build.cs",
        "publish-client",
        "--rid", rid,
        "--server", ServerUrl);

    if (code != 0)
        return code;

    // All RID binaries must describe the same source snapshot.
    if (Git("rev-parse", "HEAD").Trim().ToLowerInvariant() != commit ||
        (Git("status", "--porcelain", "--untracked-files=normal").Length != 0) != dirty)
        throw new InvalidOperationException("Source changed while publishing this release; discarding the manifest.");

    var sourceName = isWin ? "JarvisClient.exe" : "JarvisClient";
    var targetName = isWin ? $"JarvisClient-{rid}.exe" : $"JarvisClient-{rid}";

    var source = Path.Combine(root, "artifacts", "client", rid, sourceName);
    var target = Path.Combine(downloads, targetName);

    if (!File.Exists(source))
    {
        Console.Error.WriteLine($"Expected published client was not found: {source}");
        return 1;
    }

    var immutableTarget = Path.Combine(immutableDirectory, targetName);
    var sourceHash = Sha256(source);
    if (File.Exists(immutableTarget))
    {
        if (Sha256(immutableTarget) != sourceHash)
            throw new InvalidOperationException($"Immutable release artifact already exists with a different SHA-256: {immutableTarget}");
    }
    else
        File.Copy(source, immutableTarget, overwrite: false);

    File.Copy(source, target, overwrite: true);
    if (Sha256(target) != sourceHash)
        throw new InvalidOperationException($"Copied download hash mismatch: {target}");

    artifacts.Add((rid, targetName, $"releases/{releaseId}/{targetName}", sourceHash, new FileInfo(source).Length));

    if (!isWin && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(target, mode);
        File.SetUnixFileMode(immutableTarget, mode);
    }
}

// The current pointer is written only after every immutable artifact and
// stable update-channel alias has been verified.
// File-based scripts use a reflection-disabled runtime. Write typed JSON
// directly; don't depend on reflection-based JsonSerializer or source generation.
// The current pointer is atomically replaced only after all cross-RID outputs
// and their immutable hashes have been verified.
var latestPath = Path.Combine(downloads, "latest.json");
var stagedLatestPath = latestPath + ".tmp";
using (var stream = new FileStream(stagedLatestPath, FileMode.Create, FileAccess.Write, FileShare.None))
using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("applicationVersion", version);
    writer.WriteString("gitCommit", commit);
    writer.WriteBoolean("dirty", dirty);
    writer.WriteString("releaseId", releaseId);
    writer.WriteStartArray("artifacts");
    foreach (var artifact in artifacts)
    {
        writer.WriteStartObject();
        writer.WriteString("rid", artifact.Rid);
        writer.WriteString("fileName", artifact.FileName);
        writer.WriteString("releasePath", artifact.ReleasePath);
        writer.WriteString("sha256", artifact.Sha256);
        writer.WriteNumber("bytes", artifact.Bytes);
        writer.WriteEndObject();
    }
    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.Flush();
    stream.Flush(flushToDisk: true);
}
File.Move(stagedLatestPath, latestPath, overwrite: true);
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

string Git(params string[] arguments)
{
    using var process = new Process();
    process.StartInfo.FileName = "git";
    process.StartInfo.WorkingDirectory = root;
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;
    foreach (var argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException("Git source identity failed: " + error);
    return output;
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
