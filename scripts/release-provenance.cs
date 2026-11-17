#!/usr/bin/env dotnet
// Release provenance is intentionally a local, explicit publishing gate. No credentials or deployment.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var root = FindRepositoryRoot();
const string MetadataName = "RELEASE-PROVENANCE.json";
const string ChecksumsName = "SHA256SUMS";

try
{
    if (args.Length == 0)
        throw new InvalidOperationException("Usage: preflight [expectedCommit] | stamp kind rid configuration outputDir serverUrl expectedCommit | collect sourceFile targetFile | verify [--complete]");

    switch (args[0])
    {
        case "preflight":
            if (args.Length is < 1 or > 2) throw new InvalidOperationException("preflight [expectedCommit]");
            var head = RequireClean(args.Length == 2 ? args[1] : null);
            Console.WriteLine($"Clean release source: {head}");
            break;
        case "stamp":
            if (args.Length != 7) throw new InvalidOperationException("stamp kind rid configuration outputDir serverUrl expectedCommit");
            Stamp(args[1], args[2], args[3], args[4], args[5], args[6]);
            break;
        case "collect":
            if (args.Length != 3) throw new InvalidOperationException("collect sourceFile targetFile");
            Collect(args[1], args[2]);
            break;
        case "verify":
            if (args.Length > 2 || (args.Length == 2 && args[1] != "--complete"))
                throw new InvalidOperationException("verify [--complete]");
            VerifyDownloads(Path.Combine(root, "artifacts", "downloads"), args.Length == 2);
            Console.WriteLine("Release hashes and provenance verified.");
            break;
        default:
            throw new InvalidOperationException($"Unknown command: {args[0]}");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Release provenance rejected: {ex.Message}");
    return 1;
}

void Stamp(string kind, string rid, string configuration, string outputDir, string serverUrl, string expectedCommit)
{
    if (kind != "client" && kind != "server") throw new InvalidOperationException("Expected client or server.");
    if (configuration != "Release") throw new InvalidOperationException("Only Release builds can be stamped.");
    if (!new[] { "linux-x64", "win-x64", "osx-arm64" }.Contains(rid))
        throw new InvalidOperationException("Unrecognized runtime identifier.");
    if (kind == "server" && serverUrl != "-")
        throw new InvalidOperationException("Server builds must not include a client URL.");
    if (kind == "client" && (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var url) || url.Scheme != "https"))
        throw new InvalidOperationException("Client release requires an absolute HTTPS server URL.");

    var commit = RequireClean(expectedCommit);
    var fileName = kind == "client" ? (rid.StartsWith("win-", StringComparison.Ordinal) ? "JarvisClient.exe" : "JarvisClient")
                                    : (rid.StartsWith("win-", StringComparison.Ordinal) ? "JarvisServer.exe" : "JarvisServer");
    var output = ResolveInsideRoot(outputDir);
    var artifact = Path.Combine(output, fileName);
    if (!File.Exists(artifact)) throw new InvalidOperationException($"Missing expected publish output: {artifact}");
    var data = NewMetadata(commit, [new Artifact(fileName, Hash(artifact), kind, rid, configuration, serverUrl)]);
    WriteAtomic(Path.Combine(output, MetadataName), JsonSerializer.Serialize(data, ReleaseJsonContext.Default.ReleaseMetadata) + "\n");
    Console.WriteLine($"Stamped {kind}/{rid} from {commit}: {data.Artifacts[0].Sha256}");
}

void Collect(string sourceFile, string targetFile)
{
    var commit = RequireClean();
    if (Path.GetFileName(targetFile) != targetFile || targetFile is "." or "..")
        throw new InvalidOperationException("Target must be a basename.");
    var validNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "JarvisClient-linux-x64", "JarvisClient-win-x64.exe", "JarvisClient-osx-arm64", "JarvisServer-linux-x64"
    };
    if (!validNames.Contains(targetFile)) throw new InvalidOperationException("Unknown downloadable artifact name.");

    var source = ResolveInsideRoot(sourceFile);
    if (!File.Exists(source)) throw new InvalidOperationException($"Missing publish source: {source}");
    var stamp = ReadMetadata(Path.Combine(Path.GetDirectoryName(source)!, MetadataName));
    if (stamp.Artifacts.Count != 1) throw new InvalidOperationException("Expected exactly one stamped artifact.");
    var original = stamp.Artifacts[0];
    if (original.FileName != Path.GetFileName(source) || !StringEqualsHash(original.Sha256, Hash(source)))
        throw new InvalidOperationException("The binary does not match its build stamp.");
    if (stamp.SourceCommit != commit || stamp.SchemaVersion != 1)
        throw new InvalidOperationException("Source stamp belongs to another commit.");
    if (original.Configuration != "Release") throw new InvalidOperationException("Non-Release download refused.");
    if (original.Kind == "client" && targetFile != $"JarvisClient-{original.Rid}" + (original.Rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : ""))
        throw new InvalidOperationException("Client RID and target name disagree.");
    if (original.Kind == "server" && (original.Rid != "linux-x64" || targetFile != "JarvisServer-linux-x64"))
        throw new InvalidOperationException("Server RID and target name disagree.");
    if (stamp.SdkVersion != SdkVersion())
        throw new InvalidOperationException("Build SDK changed between stamping and collection.");
    if (stamp.DependencyLocksSha256 != DependencyLocksSha256())
        throw new InvalidOperationException("Dependency lockfiles changed between stamping and collection.");

    var directory = Path.Combine(root, "artifacts", "downloads");
    Directory.CreateDirectory(directory);
    var manifestPath = Path.Combine(directory, MetadataName);
    ReleaseMetadata current;
    if (File.Exists(manifestPath))
    {
        current = VerifyDownloads(directory, false);
        if (current.SourceCommit != stamp.SourceCommit || current.SdkVersion != stamp.SdkVersion ||
            current.SourceRef != stamp.SourceRef || current.DependencyLocksSha256 != stamp.DependencyLocksSha256)
            throw new InvalidOperationException("Existing downloads use different source/ref/SDK/dependency locks; clear artifacts/downloads and rebuild as one release.");
    }
    else
    {
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("Unstamped files already exist in artifacts/downloads; clear them first.");
        current = stamp with { Artifacts = [] };
    }

    var updatedArtifact = original with { FileName = targetFile };
    if (current.Artifacts.Any(x => x.FileName != targetFile && (x.Kind == updatedArtifact.Kind && x.Rid == updatedArtifact.Rid)))
        throw new InvalidOperationException("Duplicate kind/RID in download manifest.");
    var sameType = current.Artifacts.Where(x => x.Kind == updatedArtifact.Kind).ToList();
    if (sameType.Any(x => x.Configuration != updatedArtifact.Configuration ||
        (x.Kind == "client" && x.ServerUrl != updatedArtifact.ServerUrl)))
        throw new InvalidOperationException("Download bundle mixes configuration or embedded client URLs.");

    var destination = Path.Combine(directory, targetFile);
    var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
    try
    {
        File.Copy(source, staging);
        if (!StringEqualsHash(Hash(staging), updatedArtifact.Sha256))
            throw new InvalidOperationException("Copied binary hash differs from the build stamp.");
        if (!OperatingSystem.IsWindows() && !targetFile.EndsWith(".exe", StringComparison.Ordinal))
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(staging, destination, true);
    }
    finally { if (File.Exists(staging)) File.Delete(staging); }

    var entries = current.Artifacts.Where(x => x.FileName != targetFile)
        .Append(updatedArtifact).OrderBy(x => x.FileName, StringComparer.Ordinal).ToList();
    var updated = current with { Artifacts = entries };
    WriteAtomic(manifestPath, JsonSerializer.Serialize(updated, ReleaseJsonContext.Default.ReleaseMetadata) + "\n");
    WriteAtomic(Path.Combine(directory, ChecksumsName), Checksums(directory, updated));
    VerifyDownloads(directory, false);
    Console.WriteLine($"Collected {targetFile} ({updatedArtifact.Sha256}) from {commit}");
}

ReleaseMetadata VerifyDownloads(string directory, bool requireComplete)
{
    var manifestPath = Path.Combine(directory, MetadataName);
    var sumsPath = Path.Combine(directory, ChecksumsName);
    var data = ReadMetadata(manifestPath);
    if (data.SchemaVersion != 1 || !IsSha(data.SourceCommit) || string.IsNullOrWhiteSpace(data.SdkVersion) ||
        string.IsNullOrWhiteSpace(data.SourceRef) || !IsSha(data.DependencyLocksSha256))
        throw new InvalidOperationException("Invalid release manifest identity.");
    if (data.Artifacts.Count == 0) throw new InvalidOperationException("Empty release manifest.");
    if (data.Artifacts.Select(x => x.FileName).Distinct(StringComparer.Ordinal).Count() != data.Artifacts.Count)
        throw new InvalidOperationException("Duplicate artifact entries.");
    var allowed = new HashSet<string>(data.Artifacts.Select(x => x.FileName), StringComparer.Ordinal)
        { MetadataName, ChecksumsName };
    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
    {
        if (!File.Exists(entry) || !allowed.Contains(Path.GetFileName(entry)))
            throw new InvalidOperationException($"Unexpected file in release bundle: {entry}");
    }
    foreach (var a in data.Artifacts)
    {
        if (a.FileName != Path.GetFileName(a.FileName) || a.FileName is "." or ".." ||
            a.Configuration != "Release" || !IsSha(a.Sha256) || ExpectedDownloadName(a) != a.FileName ||
            (a.Kind == "server" && a.ServerUrl != "-") ||
            (a.Kind == "client" && (!Uri.TryCreate(a.ServerUrl, UriKind.Absolute, out var url) || url.Scheme != "https")))
            throw new InvalidOperationException("Invalid artifact metadata.");
        var path = Path.Combine(directory, a.FileName);
        if (!File.Exists(path) || !StringEqualsHash(Hash(path), a.Sha256))
            throw new InvalidOperationException($"Checksum mismatch or absent file: {a.FileName}");
    }
    if (!File.Exists(sumsPath) || File.ReadAllText(sumsPath) != Checksums(directory, data))
        throw new InvalidOperationException("SHA256SUMS is not the canonical checksum listing for this bundle.");
    if (requireComplete && !new[] { "JarvisClient-linux-x64", "JarvisClient-win-x64.exe",
            "JarvisClient-osx-arm64", "JarvisServer-linux-x64" }.All(allowed.Contains))
        throw new InvalidOperationException("Complete release requires all three clients and the Linux server.");
    return data;
}

string Checksums(string directory, ReleaseMetadata metadata)
{
    var entries = metadata.Artifacts.Select(x => (FileName: x.FileName, Sha256: x.Sha256))
        .Append((FileName: MetadataName, Sha256: Hash(Path.Combine(directory, MetadataName))))
        .OrderBy(x => x.FileName, StringComparer.Ordinal)
        .Select(x => $"{x.Sha256}  {x.FileName}");
    return string.Join("\n", entries) + "\n";
}

ReleaseMetadata ReadMetadata(string path)
{
    if (!File.Exists(path)) throw new InvalidOperationException($"Missing release provenance: {path}");
    var data = JsonSerializer.Deserialize(File.ReadAllText(path), ReleaseJsonContext.Default.ReleaseMetadata);
    if (data is null || data.Artifacts is null) throw new InvalidOperationException("Unreadable provenance.");
    return data;
}

string? ExpectedDownloadName(Artifact artifact) =>
    (artifact.Kind, artifact.Rid) switch
    {
        ("client", "linux-x64") => "JarvisClient-linux-x64",
        ("client", "win-x64") => "JarvisClient-win-x64.exe",
        ("client", "osx-arm64") => "JarvisClient-osx-arm64",
        ("server", "linux-x64") => "JarvisServer-linux-x64",
        _ => null
    };

ReleaseMetadata NewMetadata(string commit, List<Artifact> artifacts) =>
    new(1, commit, GitOptional("symbolic-ref", "--quiet", "--short", "HEAD") ?? "HEAD", SdkVersion(), DependencyLocksSha256(), artifacts);

string DependencyLocksSha256()
{
    string[] paths = ["Client/packages.lock.json", "Client.Tests/packages.lock.json",
        "Common/packages.lock.json", "Server/packages.lock.json", "Server.Tests/packages.lock.json"];
    var records = new List<string>();
    foreach (var path in paths)
    {
        var fullPath = Path.Combine(root, path);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException($"Missing release dependency lockfile: {path}");
        records.Add($"{path} {Hash(fullPath)}");
    }
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records) + "\n"))).ToLowerInvariant();
}

string RequireClean(string? expectedCommit = null)
{
    var workingRoot = Git("rev-parse", "--show-toplevel").Trim();
    if (!string.Equals(Path.GetFullPath(workingRoot), Path.GetFullPath(root), StringComparison.Ordinal))
        throw new InvalidOperationException("Worktree root differs from the release directory.");
    var commit = Git("rev-parse", "--verify", "HEAD").Trim().ToLowerInvariant();
    if (!IsSha(commit)) throw new InvalidOperationException("Invalid Git HEAD.");
    if (expectedCommit is not null && commit != expectedCommit)
        throw new InvalidOperationException("Git HEAD changed during publishing.");
    var dirty = Git("status", "--porcelain", "--untracked-files=normal");
    if (!string.IsNullOrWhiteSpace(dirty))
        throw new InvalidOperationException($"Git worktree is not clean; first change: {dirty.Trim().Split('\n')[0]}");
    return commit;
}

string ResolveInsideRoot(string path)
{
    var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException("Path must be within repository worktree.");
    return resolved;
}

string Git(params string[] arguments) => Run("git", arguments, required: true)!;
string? GitOptional(params string[] arguments) => Run("git", arguments, required: false);
string SdkVersion() => Run("dotnet", ["--version"], required: true)!.Trim();

string? Run(string command, IEnumerable<string> arguments, bool required)
{
    using var p = new Process();
    p.StartInfo.FileName = command;
    p.StartInfo.WorkingDirectory = root;
    p.StartInfo.UseShellExecute = false;
    p.StartInfo.RedirectStandardOutput = true;
    p.StartInfo.RedirectStandardError = true;
    foreach (var argument in arguments) p.StartInfo.ArgumentList.Add(argument);
    if (!p.Start()) throw new InvalidOperationException($"Could not start {command}");
    var output = p.StandardOutput.ReadToEnd();
    var error = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(15000)) { p.Kill(entireProcessTree: true); throw new InvalidOperationException($"{command} timed out."); }
    if (p.ExitCode != 0)
    {
        if (!required) return null;
        throw new InvalidOperationException($"{command} failed: {error.Trim()}");
    }
    return output.Trim();
}

bool IsSha(string value) => (value.Length == 40 || value.Length == 64) && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
bool StringEqualsHash(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);
string Hash(string path)
{
    using var input = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
}
void WriteAtomic(string path, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
        File.WriteAllText(temp, value, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    finally { if (File.Exists(temp)) File.Delete(temp); }
}
string FindRepositoryRoot()
{
    var here = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (here is not null)
    {
        if (File.Exists(Path.Combine(here.FullName, "Jarvis.slnx"))) return here.FullName;
        here = here.Parent;
    }
    throw new InvalidOperationException("Run from the Jarvis checkout.");
}

sealed record Artifact(string FileName, string Sha256, string Kind, string Rid, string Configuration, string ServerUrl);
sealed record ReleaseMetadata(int SchemaVersion, string SourceCommit, string SourceRef, string SdkVersion, string DependencyLocksSha256, List<Artifact> Artifacts);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseMetadata))]
internal partial class ReleaseJsonContext : JsonSerializerContext
{
}
