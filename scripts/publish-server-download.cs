#!/usr/bin/env dotnet
using System.Diagnostics;
using System.Security.Cryptography;

var root = Directory.GetCurrentDirectory();
var process = Process.Start(new ProcessStartInfo("dotnet")
{
    ArgumentList = { "scripts/build.cs", "publish-server", "--rid", "linux-x64" },
    UseShellExecute = false
}) ?? throw new Exception("Could not start server publish.");
process.WaitForExit();
if (process.ExitCode != 0) return process.ExitCode;
var dir = Path.Combine(root, "artifacts", "downloads");
Directory.CreateDirectory(dir);
var file = "JarvisServer-linux-x64";
var source = Path.Combine(root, "artifacts", "server", "linux-x64", "JarvisServer");
File.Copy(source, Path.Combine(dir, file), true);
using var stream = File.OpenRead(Path.Combine(dir, file));
var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
var manifest = Path.Combine(dir, "SHA256SUMS");
var lines = File.Exists(manifest)
    ? File.ReadAllLines(manifest).Where(line => !line.EndsWith("  " + file, StringComparison.Ordinal)).ToList()
    : new List<string>();
lines.Add(hash + "  " + file);
File.WriteAllLines(manifest, lines.Order(StringComparer.Ordinal));
Console.WriteLine($"Published {file}: {hash}");
return 0;
