using System.Collections.Immutable;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace JarvisClient;

public readonly struct Context(string projectDirectory)
{
    public ProjDirectory ProjectDirectory => new(projectDirectory);

    public ImmutableArray<string> ProjectFiles { get; } =
        ["readme.md", "notes.md", "todo.md", "bob_notes.txt"];

    public ImmutableArray<Func<string, bool>> FolderFilters { get; } =
    [
        s => !s.StartsWith('.'),
        s => s != "bin",
        s => s != "obj"
    ];

    public string HubUrl => "https://jarvis.kehlet.dev/client";
}

public interface IHasContext
{
    Context Context { get; }
}

public interface IHasHub<TRuntime>
    where TRuntime : struct, IHasHub<TRuntime>, IHasContext
{
    public Effect<TRuntime, HubConnection> Connect() =>
        asEffect<TRuntime, HubConnection>() <<
        (
            runtime => new HubConnectionBuilder()
                       .AddJsonProtocol()
                       .WithUrl(runtime.Context.HubUrl)
                       .Build()
                       .Apply(x => effect<TRuntime, HubConnection>(_ => ok(x)))
        );
}
