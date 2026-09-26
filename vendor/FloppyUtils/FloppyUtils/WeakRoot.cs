using FloppyUtils.SourceGen.Attributes;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// Utility to perform weak ref reflection-based accesses, compatible with ALC unloads.
/// To be used with structs annotated with <see cref="WeakProxyAttribute"/>.
/// </summary>
/// <param name="tag"></param>
/// <param name="refresher"></param>
public sealed class WeakRoot(string tag, Func<AssemblyLoadContext?> refresher) : IDisposable
{
    internal const BindingFlags _BindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private readonly string _tag = tag;
    private readonly Func<AssemblyLoadContext?> _refresher = refresher;
    private readonly Lock _lock = new();
    private readonly Dictionary<(Type, string), Delegate?> _methodCache = [];
    private readonly Dictionary<(Type, string), MemberInfo?> _memberCache = [];
    private AssemblyLoadContext? _alc;

    public event Action? OnCleanup;

    public void Dispose()
    {
        lock (_lock)
        {
            _methodCache.Clear();
            _memberCache.Clear();

            if (_alc is { } alc)
            {
                _alc = null;
                alc?.Unloading -= OnAlcUnload;
                OnCleanup?.Invoke();
            }
        }
    }

    /// <summary>
    /// Performs a manual check for ALC .
    /// </summary>
    /// <returns></returns>
    public bool Refresh()
    {
        lock (_lock)
        {
            if (_alc is not null)
            {
                return true;
            }

            Dispose();

            AssemblyLoadContext? alc;

            try
            {
                alc = _refresher();
            }
            catch (Exception e)
            {
                FService.PluginLog.Error($"{Info()} failed to refresh: {e}");
                return false;
            }

            if (alc is null)
            {
                FService.PluginLog.Info($"{Info()} refresh returned null");
                return false;
            }

            _alc = alc;
            return true;
        }
    }

    public Type? GetType(string name)
    {
        lock (_lock)
        {
            if (_alc is not { } alc)
            {
                return null;
            }

            foreach (var asm in alc.Assemblies)
            {
                if (asm.GetType(name) is { } type)
                {
                    return type;
                }
            }

            return null;
        }
    }

    public T? GetMethod<T>(Type target, string name, Func<Type, MethodBase>? get = null) where T : Delegate
    {
        lock (_lock)
        {
            if (_methodCache.TryGetValue((target, name), out var fun))
            {
                return (T?) fun;
            }

            var method = get?.Invoke(target);
            method ??= target.GetMethods().FirstOrDefault(m => m.Name == name);
            method ??= target.GetMethod(name, _BindingFlags)!;

            var del = method?.CreateDynamicDelegate<T>();
            _methodCache[(target, name)] = del;
            return del;
        }
    }

    public MemberInfo? GetMember(Type target, string name, Func<Type, MemberInfo>? get = null)
    {
        lock (_lock)
        {
            if (_memberCache.TryGetValue((target, name), out var fun))
            {
                return fun;
            }

            var member = get?.Invoke(target);
            member ??= target.GetMember(name, _BindingFlags).First();
            return _memberCache[(target, name)] = member;
        }
    }

    public object? GetStaticMemberValue(string typeName, string name, params object?[] args)
    {
        if (GetType(typeName) is not { } type)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(GetStaticMemberValue)} failed, type not found: {typeName}");
            return null;
        }

        if (GetMember(type, name) is not { } member)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(GetStaticMemberValue)} failed, member not found: {typeName}::{name}");
            return null;
        }

        return member switch
        {
            FieldInfo field => field.GetValue(null),
            PropertyInfo property => property.GetValue(null, args),
            _ => null,
        };
    }

    public object? GetMemberValue(object proxiedValue, string name, params object?[] args)
    {
        if (GetMember(proxiedValue.GetType(), name) is not { } member)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(GetMemberValue)} failed, member not found: {proxiedValue.GetType().FullName}::{name}");
            return null;
        }

        return member switch
        {
            FieldInfo field => field.GetValue(proxiedValue),
            PropertyInfo property => property.GetValue(proxiedValue, args),
            _ => null,
        };
    }

    public void SetStaticMemberValue(string typeName, string name, params object?[] args)
    {
        if (GetType(typeName) is not { } type)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(SetStaticMemberValue)} failed, type not found: {typeName}");
            return;
        }

        if (GetMember(type, name) is not { } member)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(SetStaticMemberValue)} failed, member not found: {typeName}::{name}");
            return;
        }

        switch (member)
        {
            case FieldInfo field:
                field.SetValue(null, args[0]);
                break;
            case PropertyInfo property:
                property.SetValue(null, args.First(), args.Length > 1 ? [.. args.Skip(1)] : null);
                break;
        }
    }

    public void SetMemberValue(object proxiedValue, string name, params object?[] args)
    {
        if (GetMember(proxiedValue.GetType(), name) is not { } member)
        {
            FService.PluginLog.Warning($"{Info()} {nameof(SetMemberValue)} failed, member not found: {proxiedValue.GetType().FullName}::{name}");
            return;
        }

        switch (member)
        {
            case FieldInfo field:
                field.SetValue(proxiedValue, args[0]);
                break;
            case PropertyInfo property:
                property.SetValue(proxiedValue, args.First(), args.Length > 1 ? [.. args.Skip(1)] : null);
                break;
        }
    }

    public ITuple ConvertValueTuple(Type targetType, object proxiedValue)
    {
        var rawTuple = (ITuple) proxiedValue;
        var dstItems = new object?[rawTuple.Length];
        var rawTypes = proxiedValue.GetType().GenericTypeArguments;
        var dstTypes = targetType.GenericTypeArguments;

        for (var i = 0; i < rawTuple.Length; i++)
        {
            var raw = rawTuple[i];

            if (dstTypes[i].IsAssignableFrom(rawTypes[i]))
            {
                dstItems[i] = raw;
            }
            else if (raw is not null)
            {
                var dstType = dstTypes[i];

                var isNullable = dstType.FullName?.StartsWith("System.Nullable`1[[") ?? false;

                if (isNullable)
                {
                    dstType = dstType.GenericTypeArguments[0];
                }

                dstItems[i] = Activator.CreateInstance(dstType, [this, raw]);

                if (isNullable)
                {
                    dstItems[i] = Activator.CreateInstance(dstTypes[i], [dstItems[i]!]);
                }
            }
            else
            {
                dstItems[i] = null;
            }
        }

        return (ITuple) Activator.CreateInstance(targetType, dstItems)!;
    }

    private string Info() => $"{nameof(WeakRoot)}({_tag})";

    private void OnAlcUnload(AssemblyLoadContext obj)
    {
        Dispose();
    }

    /// <summary>
    /// Interface implemented by structs decorated with <see cref="WeakProxyAttribute"/>.
    /// </summary>
    public interface IProxy
    {
        WeakRoot Root { get; }
        object Value { get; }
    }

    /// <summary>
    /// Lazily reobtainable weak reference.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="root"></param>
    /// <param name="refresher"></param>
    /// <param name="ctx"></param>
    public sealed class Lazy<T>(WeakRoot root, Func<WeakRoot, T?> refresher, [CallerArgumentExpression(nameof(refresher))] string? ctx = null) where T : class
    {
        private readonly WeakRoot _root = root;
        private readonly Func<WeakRoot, T?> _refresher = refresher;
        private readonly string _ctx = ctx ?? typeof(T).FullName ?? "?";

        private WeakReference<T>? _value;

        public bool TryGet([NotNullWhen(true)] out T? value)
        {
            if (_value?.TryGetTarget(out value) ?? false)
            {
                return true;
            }

            _value = null;

            try
            {
                value = _refresher(_root);
            }
            catch (Exception e)
            {
                FService.PluginLog.Error($"{Info()} failed to refresh: {e}");
                value = null;
                return false;
            }

            if (value is null)
            {
                FService.PluginLog.Warning($"{Info()} returned null");
                _value = null;
                return false;
            }

            _value = new(value);
            return true;
        }

        private string Info() => $"{_root.Info()}.Proxy({_ctx})";
    }
}
