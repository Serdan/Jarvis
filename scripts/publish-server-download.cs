#!/usr/bin/env dotnet
using System.Diagnostics;

// The shared publisher owns the build, evidence and final-copy boundary.
var root = FindRepositoryRoot();
var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3")
{
    UseShellExecute = false
};
start.ArgumentList.Add("-B");
start.ArgumentList.Add(Path.Combine(root, "scripts", "release-provenance-publisher-composition", "publish.py"));
start.ArgumentList.Add("JarvisServer");
foreach (var arg in args)
    start.ArgumentList.Add(arg);
using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start provenance publisher.");
process.WaitForExit();
return process.ExitCode;

string FindRepositoryRoot()
{
    foreach (var startDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var current = new DirectoryInfo(startDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Jarvis.slnx")))
                return current.FullName;
    }
    throw new InvalidOperationException("Could not find repository root containing Jarvis.slnx.");
}
