using System;
using System.Collections.Generic;
using System.Reflection;

namespace FloppyUtils.Ptrs;

public unsafe readonly struct SafeCast<TFrom>(TFrom* from) where TFrom : unmanaged
{
    private static readonly Dictionary<Guid, bool> _toRiidCache = [];

    private readonly TFrom* _from = from;

    public TTo* ToBase<TTo>() where TTo : unmanaged
    {
#if DEBUG
        if (!CanCastToBase<TTo>())
        {
            throw new InvalidCastException($"Cannot cast from {typeof(TFrom).FullName} to base {typeof(TTo).FullName}");
        }
#endif

        return (TTo*) _from;
    }

    public TTo* ToSpec<TTo>() where TTo : unmanaged
    {
#if DEBUG
        if (!SafeCast<TTo>.CanCastToBase<TFrom>())
        {
            throw new InvalidCastException($"Cannot cast from {typeof(TFrom).FullName} to spec {typeof(TTo).FullName}");
        }
#endif

        return (TTo*) _from;
    }

    public static bool CanCastToBase<TTo>() where TTo : unmanaged
    {
        if (ToTypeCache<TTo>.CanCastToBase is { } value)
        {
            return value;
        }

        value = SafeCast.InternalCanCastToBase(typeof(TFrom), typeof(TTo));
        ToTypeCache<TTo>.CanCastToBase = value;
        return value;
    }

    public static bool CanCastToBase(Guid riid)
    {
        if (_toRiidCache.TryGetValue(riid, out var value))
        {
            return value;
        }

        foreach (var iface in typeof(TFrom).GetInterfaces())
        {
            if (iface.IsNested && SafeCast.InternalHasRiid(iface.DeclaringType!, riid))
            {
                return _toRiidCache[riid] = true;
            }
        }

        return _toRiidCache[riid] = false;
    }

    private static class ToTypeCache<TTo> where TTo : unmanaged
    {
        public static bool? CanCastToBase;
    }
}

public static unsafe class SafeCast
{
    public static SafeCast<T> From<T>(T* from) where T : unmanaged
    {
        return new(from);
    }

    internal static bool InternalCanCastToBase(Type from, Type to)
    {
        if (from == to)
        {
            return true;
        }

        if (from.GetNestedType("Interface") is { } fromInterface &&
            to.GetNestedType("Interface") is { } toInterface &&
            fromInterface.IsAssignableTo(toInterface))
        {
            return true;
        }

        // TODO: Hook up to Vtbl fake RTTI
        return false;
    }

    internal static bool InternalHasRiid(Type from, Guid to)
    {
        if (from.GetProperty("TerraFX.Interop.INativeGuid.NativeGuid", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) is not { } prop)
        {
            return false;
        }

        var fromGuid = *(Guid*) Pointer.Unbox(prop.GetValue(null)!);
        return fromGuid == to;
    }
}
