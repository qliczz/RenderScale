using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FloppyUtils;

/// <summary>
/// Utilities to automatically create instances <see cref="AutoSingletonAttribute"/> types in an assembly.
/// </summary>
public static class AutoSingleton
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEnumerable<T> GetAll<T>() => GetAll<T>(Assembly.GetCallingAssembly());

    public static IEnumerable<T> GetAll<T>(Assembly asm)
    {
        var list = new List<T>();

        foreach (var t in asm.GetTypes())
        {
            if (t.IsAbstract || !typeof(T).IsAssignableFrom(t))
            {
                continue;
            }

            foreach (var init in t.GetCustomAttributes<AutoSingletonAttribute>())
            {
                var type = t;
                if (init.GenericArguments.Length != 0)
                {
                    type = type.MakeGenericType(init.GenericArguments);
                }

                list.Add((T) Get(type)!);
            }
        }

        return list;
    }

    public static object? Get(Type type)
    {
        if (!type.GetCustomAttributes<AutoSingletonAttribute>().Any())
        {
            return null;
        }

        return typeof(AutoSingleton<>).MakeGenericType(type).GetMethod("Get")!.Invoke(null, null)!;
    }
}

public static class AutoSingleton<T>
{
    private static T? _instance;

    public static T Get()
    {
        return _instance ??= Activator.CreateInstance<T>()!;
    }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public class AutoSingletonAttribute(params Type[] genericArguments) : Attribute
{
    public Type[] GenericArguments = genericArguments;
}
