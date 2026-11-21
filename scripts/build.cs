#!/usr/bin/env dotnet
#:package System.CommandLine@2.0.8

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

var root = FindRepositoryRoot();

var configurationOption = new Option<string>("--configuration")
{
    Description = "Build configuration.",
    DefaultValueFactory = _ => "Debug"
};

var publishConfigurationOption = new Option<string>("--configuration")
{
    Description = "Publish configuration.",
    DefaultValueFactory = _ => "Release"
};

var ridOption = new Option<string>("--rid")
{
    Description = "Runtime identifier, for example linux-x64, win-x64, or osx-arm64.",
    Required = true
};

var ridsOption = new Option<string[]>("--rid")
{
    Description = "Runtime identifier. Can be specified more than once.",
    AllowMultipleArgumentsPerToken = true,
    DefaultValueFactory = _ => [ "linux-x64" ]
};

var serverOption = new Option<string>("--server")
{
    Description = "Server URI to embed in the published client.",
    Required = true
};

var outputOption = new Option<string?>("--output")
{
    Description = "Output directory. Defaults to artifacts/client/<rid> for publish-client and artifacts/client for publish-clients."
};

var rootCommand = new RootCommand("Jarvis build script");

var testCommand = new Command("test", "Run the test suites.");
testCommand.Options.Add(configurationOption);
testCommand.SetAction(parseResult =>
{
    var configuration = parseResult.GetValue(configurationOption)!;
    return Test(configuration);
});
rootCommand.Subcommands.Add(testCommand);

var buildCommand = new Command("compile", "Build the full solution.");
buildCommand.Options.Add(configurationOption);
buildCommand.SetAction(parseResult =>
{
    var configuration = parseResult.GetValue(configurationOption)!;
    return Build(configuration);
});
rootCommand.Subcommands.Add(buildCommand);

var publishClientCommand = new Command("publish-client", "Publish one stamped single-file client executable.");
publishClientCommand.Options.Add(ridOption);
publishClientCommand.Options.Add(serverOption);
publishClientCommand.Options.Add(outputOption);
publishClientCommand.Options.Add(publishConfigurationOption);
publishClientCommand.SetAction(parseResult =>
{
    var rid = parseResult.GetRequiredValue(ridOption);
    var server = parseResult.GetRequiredValue(serverOption);
    var configuration = parseResult.GetValue(publishConfigurationOption)!;
    var output = parseResult.GetValue(outputOption) ?? Path.Combine("artifacts", "client", rid);

    return PublishClient(rid, server, configuration, output);
});
rootCommand.Subcommands.Add(publishClientCommand);

var publishClientsCommand = new Command("publish-clients", "Publish stamped clients for one or more RIDs.");
publishClientsCommand.Options.Add(ridsOption);
publishClientsCommand.Options.Add(serverOption);
publishClientsCommand.Options.Add(outputOption);
publishClientsCommand.Options.Add(publishConfigurationOption);
publishClientsCommand.SetAction(parseResult =>
{
    var rids = parseResult.GetValue(ridsOption) ?? [ "linux-x64" ];
    var server = parseResult.GetRequiredValue(serverOption);
    var configuration = parseResult.GetValue(publishConfigurationOption)!;
    var outputRoot = parseResult.GetValue(outputOption) ?? Path.Combine("artifacts", "client");

    return PublishClients(rids, server, configuration, outputRoot);
});
rootCommand.Subcommands.Add(publishClientsCommand);

var publishServerCommand = new Command("publish-server", "Publish one self-contained single-file server executable.");
publishServerCommand.Options.Add(ridOption);
publishServerCommand.Options.Add(outputOption);
publishServerCommand.Options.Add(publishConfigurationOption);
publishServerCommand.SetAction(parseResult =>
{
    var rid = parseResult.GetRequiredValue(ridOption);
    var configuration = parseResult.GetValue(publishConfigurationOption)!;
    var output = parseResult.GetValue(outputOption) ?? Path.Combine("artifacts", "server", rid);

    return PublishServer(rid, configuration, output);
});
rootCommand.Subcommands.Add(publishServerCommand);

var publishJarvis2LinuxCommand = new Command("publish-jarvis2-linux", "Publish linux-x64 JarvisServer and JarvisClient for jarvis2.kehlet.dev.");
publishJarvis2LinuxCommand.Options.Add(publishConfigurationOption);
publishJarvis2LinuxCommand.SetAction(parseResult =>
{
    var configuration = parseResult.GetValue(publishConfigurationOption)!;
    return PublishJarvis2Linux(configuration);
});
rootCommand.Subcommands.Add(publishJarvis2LinuxCommand);

var cleanCommand = new Command("clean", "Delete artifacts/ and run dotnet clean.");
cleanCommand.SetAction(_ => Clean());
rootCommand.Subcommands.Add(cleanCommand);

return rootCommand.Parse(args).Invoke();

int Test(string configuration)
{
    var clientCode = DotNet("test", "Client.Tests/Client.Tests.fsproj", "-c", configuration);
    if (clientCode != 0)
        return clientCode;

    return DotNet("test", "Server.Tests/Server.Tests.fsproj", "-c", configuration);
}

int Build(string configuration)
{
    return DotNet("build", "Jarvis.slnx", "-c", configuration);
}

int PublishClient(string rid, string server, string configuration, string output)
{
    // The source SHA is the code identity; a semver bump is not required for every commit.
    // Dirty worktrees must never masquerade as an approved clean release.
    var commit = Git("rev-parse", "HEAD").Trim();
    if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        throw new InvalidOperationException("Could not verify the full Git source commit.");

    var dirty = Git("status", "--porcelain", "--untracked-files=normal").Length != 0;
    var code = DotNet(
        "publish",
        "Client/Client.fsproj",
        "-c", configuration,
        "-r", rid,
        "-o", output,
        "-p:PublishSingleFile=true",
        "-p:SelfContained=true",
        "-p:Deterministic=true",
        $"-p:SourceRevisionId={commit}",
        $"-p:JarvisBuildCommit={commit}",
        $"-p:JarvisBuildRid={rid}",
        $"-p:JarvisBuildDirty={dirty.ToString().ToLowerInvariant()}",
        $"-p:JarvisServerUrl={server}"
    );
    if (code != 0)
        return code;

    return VerifyNativePublishedIdentity(rid, output, commit, dirty);
}

int VerifyNativePublishedIdentity(string rid, string outputDirectory, string commit, bool dirty)
{
    var hostRid =
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" :
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" :
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "";

    // Cross-RID builds cannot be executed on this host; their identity comes
    // from the same explicit compiler properties and release manifest.
    if (rid != hostRid)
        return 0;

    var binary = Path.Combine(root, outputDirectory, rid.StartsWith("win-", StringComparison.Ordinal) ? "JarvisClient.exe" : "JarvisClient");
    var version = XDocument.Load(Path.Combine(root, "Client", "Client.fsproj"))
        .Descendants("Version").First().Value.Trim();
    using var process = new Process();
    process.StartInfo.FileName = binary;
    process.StartInfo.WorkingDirectory = root;
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;
    process.StartInfo.ArgumentList.Add("--version");
    process.Start();
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(15_000))
    {
        process.Kill(entireProcessTree: true);
        Console.Error.WriteLine($"Published {rid} --version did not exit within 15 seconds.");
        return 1;
    }
    var stdout = outputTask.GetAwaiter().GetResult();
    var stderr = errorTask.GetAwaiter().GetResult();
    if (process.ExitCode != 0
        || !stdout.Contains($"Jarvis Client {version}", StringComparison.Ordinal)
        || !stdout.Contains($"Git commit: {commit}", StringComparison.Ordinal)
        || !stdout.Contains($"RID: {rid}", StringComparison.Ordinal)
        || !stdout.Contains("Protocol: ", StringComparison.Ordinal)
        || !stdout.Contains(dirty ? "Build: dirty/development" : "Build: clean", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Published {rid} offline build identity did not match source stamp. {stderr}");
        return 1;
    }

    Console.WriteLine($"Verified offline build identity for {rid}: {version} @ {commit} ({(dirty ? "dirty" : "clean")}).");
    return 0;
}

int PublishClients(IReadOnlyCollection<string> rids, string server, string configuration, string outputRoot)
{
    foreach (var rid in rids)
    {
        var code = PublishClient(rid, server, configuration, Path.Combine(outputRoot, rid));

        if (code != 0)
            return code;
    }

    return 0;
}

int PublishServer(string rid, string configuration, string output)
{
    return DotNet(
        "publish",
        "Server/Server.fsproj",
        "-c", configuration,
        "-r", rid,
        "-o", output,
        "-p:PublishSingleFile=true",
        "-p:SelfContained=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true"
    );
}

int PublishJarvis2Linux(string configuration)
{
    const string rid = "linux-x64";
    const string server = "https://jarvis2.kehlet.dev/client";

    var serverCode = PublishServer(rid, configuration, Path.Combine("artifacts", "server", rid));
    if (serverCode != 0)
        return serverCode;

    return PublishClient(rid, server, configuration, Path.Combine("artifacts", "client", rid));
}

int Clean()
{
    var artifacts = Path.Combine(root, "artifacts");

    if (Directory.Exists(artifacts))
    {
        Directory.Delete(artifacts, recursive: true);
        Console.WriteLine($"Deleted {artifacts}");
    }

    return DotNet("clean", "Jarvis.slnx");
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

    throw new InvalidOperationException("Could not find repository root containing Jarvis.slnx.");
}

string QuoteIfNeeded(string value)
{
    if (value.Any(char.IsWhiteSpace))
        return '"' + value.Replace("\"", "\\\"") + '"';

    return value;
}
