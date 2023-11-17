using System.Collections;

namespace Shared;

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
