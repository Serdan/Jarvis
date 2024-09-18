using Kehlet.Generators;

namespace JarvisClient;

[DefaultImplementation]
public readonly partial struct Runtime(Context context) : IHasFileSystem<Runtime>, IHasContext, IHasConsole<Runtime>
{
    public AsyncEffect<Runtime, IFileIO> File => okAsyncEffect<Runtime, IFileIO>(new FileIO());

    public Effect<Runtime, IDirectoryIO> Directory => okEffect<Runtime, IDirectoryIO>(new DirectoryIO());

    public Context Context => context;

    public Effect<Runtime, IConsoleIO> Console =>
        okEffect(() => (IConsoleIO) new ConsoleIO());
}
