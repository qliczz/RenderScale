using FloppyUtils.Ptrs;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FloppyUtils;

/// <summary>
/// Utility to register and check fake RTTI metadata.
/// </summary>
public abstract class Vtbl
{
    protected static readonly Dictionary<Type, Vtbl> _allByType = [];
    protected static readonly Dictionary<nint, Type> _allByAddr = [];

    protected readonly Type _spec;
    protected readonly HashSet<Type> _base = [];
    protected readonly HashSet<Type> _inheritedBy = [];

    protected nint _addr;

    public IEnumerable<Vtbl> Base => _base.Select(static t => GetOrCreateUnknown(t));

    internal Vtbl(Type spec)
    {
        _spec = spec;

        if (_allByType.TryGetValue(spec, out var old))
        {
            _base = old._base;
            _inheritedBy = old._inheritedBy;
            _addr = old._addr;
        }

        _allByType[spec] = this;
    }

    public abstract string GetFriendlyName();

    public bool Is(nint addr)
    {
        if (addr == 0)
        {
            return false;
        }

        if (_addr != 0 && _addr == addr)
        {
            return true;
        }

        // No RTTI means we need to map out inheritance ourselves...
        foreach (var sub in _inheritedBy)
        {
            if (GetOrCreateUnknown(sub).Is(addr))
            {
                return true;
            }
        }

        return false;
    }

    public Vtbl AddBase(Type spec)
    {
        _base.Add(spec);
        GetOrCreateUnknown(spec)._inheritedBy.Add(_spec);
        return this;
    }

    public static Vtbl<TSpec, TVtbl> Setup<TSpec, TVtbl>(nint addr) where TSpec : unmanaged where TVtbl : unmanaged
    {
        if (_allByType.TryGetValue(typeof(TSpec), out var vtbl) && vtbl is Vtbl<TSpec, TVtbl> inst)
        {
            return inst;
        }

        inst = new Vtbl<TSpec, TVtbl>();
        inst.Setup(addr);
        return inst;
    }

    public static Vtbl<TSpec, TVtbl> Get<TSpec, TVtbl>() where TSpec : unmanaged where TVtbl : unmanaged
    {
        Static<TSpec>.Init();
        Static<TVtbl>.Init();

        if (!_allByType.TryGetValue(typeof(TSpec), out var vtbl) ||
            vtbl is not Vtbl<TSpec, TVtbl> inst)
        {
            return new Vtbl<TSpec, TVtbl>();
        }

        return inst;
    }

    /// <summary>
    /// Tries to find the Vtbl matching the given address, be it a vtbl or an instance address.
    /// This assumes that the necessary Vtbl information has been set up already.
    /// </summary>
    /// <param name="addr"></param>
    /// <param name="ctx"></param>
    /// <returns></returns>
    public static Vtbl? Find(nint addr, out string ctx)
    {
        if (addr != 0)
        {
            if (_allByAddr.TryGetValue(addr, out var type) &&
                _allByType.TryGetValue(type, out var vtbl))
            {
                ctx = vtbl.GetFriendlyName();
                return vtbl;
            }

            if (Ptr.For<nint>(addr).TryRead(out var vptr) &&
                _allByAddr.TryGetValue(vptr, out type) &&
                _allByType.TryGetValue(type, out vtbl))
            {
                ctx = vtbl.GetFriendlyName() + "*";
                return vtbl;
            }
        }

        ctx = "";
        return null;
    }

    internal static void ThrowIfNotSetUp([NotNull] Vtbl? vtbl, Type spec)
    {
        if (vtbl == null || vtbl._addr == 0)
        {
            throw new Exception($"Vtbl<{spec.Name}> hasn't been set up yet");
        }
    }

    internal static Vtbl GetOrCreateUnknown(Type spec)
    {
        if (_allByType.TryGetValue(spec, out var vtbl))
        {
            return vtbl;
        }

        return new VtblUnknown(spec);
    }

    private static class Static<T> where T : unmanaged
    {
        private static bool _ready = false;

        public static void Init()
        {
            if (_ready)
            {
                return;
            }

            foreach (var setup in typeof(T).GetCustomAttributes<VtblSetupAttribute>())
            {
                RuntimeHelpers.RunClassConstructor(setup.Type.TypeHandle);
            }
        }
    }
}

public unsafe class Vtbl<TSpec, TVtbl> : Vtbl where TSpec : unmanaged where TVtbl : unmanaged
{
    public TVtbl* Addr
    {
        get
        {
            ThrowIfNotSetUp(this, _spec);
            return (TVtbl*) _addr;
        }
    }

    internal Vtbl() : base(typeof(TSpec))
    {
    }

    public override string GetFriendlyName()
    {
        return $"Vtbl<{typeof(TSpec).Name},{typeof(TVtbl).Name}>";
    }

    public Vtbl<TSpec, TVtbl> AddBase<TBase>() where TBase : unmanaged
    {
        AddBase(typeof(TBase));
        return this;
    }

    public bool Is(TVtbl* addr) => Is((nint) addr);

    internal void Setup(nint addr)
    {
        if (_addr != 0)
        {
            return;
        }

        _addr = addr;
        _allByAddr[addr] = _spec;
        FService.PluginLog.Debug($"Registered vtbl: {addr.ToDebugString()}");
    }
}

internal sealed class VtblUnknown : Vtbl
{
    internal VtblUnknown(Type spec) : base(spec)
    {
    }

    public override string GetFriendlyName()
    {
        return $"VtblUnknown<{_spec.Name}>";
    }
}

/// <summary>
/// Indicates which static class is responsible for vtbl metadata setup.
/// </summary>
/// <param name="type"></param>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public class VtblSetupAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}
