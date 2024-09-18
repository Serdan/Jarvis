using System.Collections;
using Kehlet.Functional;
using Kehlet.Generators;

namespace Shared;

[FromStaticMembers(typeof(Console), implement: true, voidType: typeof(Unit))]
public partial interface IConsole;

public static class FunctionalConsole
{
    public static T Write<T>(T value)
    {
        Console.Write(DateTimeOffset.Now + ": ");
        Console.Write(value);
        return value;
    }

    public static T WriteLine<T>(T value)
    {
        Console.Write(DateTimeOffset.Now + ": ");
        Console.WriteLine(value);
        return value;
    }

    public static T WriteList<T>(T list)
        where T : IEnumerable
    {
        Console.WriteLine(DateTimeOffset.Now + ": ");
        foreach (var item in list)
        {
            Console.WriteLine(item);
        }

        return list;
    }
}
