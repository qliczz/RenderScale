using FloppyUtils.Ptrs;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;

namespace FloppyUtils;

/// <summary>
/// Utility for keeping objects in memory, f.e. invoking closures from static contexts.
/// </summary>
public sealed class LifetimeHelper : IDisposable
{
    private readonly ConcurrentDictionary<nint, object> _mapped = [];
    private readonly Manager _manager;
    private bool _alive = true;

    public nint Handle { get; private set; }

    private LifetimeHelper(Manager manager, nint handle)
    {
        _manager = manager;
        Handle = handle;
    }

    public event Action<LifetimeHelper>? OnDispose;

    public void Dispose()
    {
        if (!_alive)
        {
            return;
        }

        _alive = false;

        _manager._global.TryRemove(Handle, out _);
        OnDispose?.Invoke(this);
    }

    public CallbackPtr<TNative> Set<TNative>(TNative key, object managed) where TNative : Delegate
        => new(Set(Marshal.GetFunctionPointerForDelegate(key), managed));

    public nint Set(nint key, object managed)
    {
        _mapped[key] = managed;
        return key;
    }

    public bool TryGet<TNative, TManaged>(TNative key, [NotNullWhen(true)] out TManaged managed) where TNative : Delegate where TManaged : class
        => TryGet(Marshal.GetFunctionPointerForDelegate(key), out managed!);

    public bool TryGet<TManaged>(nint key, [NotNullWhen(true)] out TManaged managed) where TManaged : class
    {
        if (_mapped.TryGetValue(key, out var managedRaw))
        {
            managed = (TManaged) managedRaw;
            return true;
        }

        managed = null!;
        return false;
    }

    public TManaged GetOrSet<TNative, TManaged>(TNative key, Func<TNative, TManaged> factory) where TNative : Delegate where TManaged : class
    {
        return GetOrSet(Marshal.GetFunctionPointerForDelegate(key), _ => factory(key));
    }

    public TManaged GetOrSet<TManaged>(nint key, Func<nint, TManaged> factory) where TManaged : class
    {
        return (TManaged) _mapped.GetOrAdd(key, factory);
    }

    public void Unset<TNative>(TNative nativeHandler) where TNative : Delegate
        => Unset(Marshal.GetFunctionPointerForDelegate(nativeHandler));

    public void Unset(nint nativeHandler)
    {
        _mapped.TryRemove(nativeHandler, out _);
    }

    public sealed class Manager : IDisposable
    {
        internal readonly ConcurrentDictionary<nint, LifetimeHelper> _global = [];
        private volatile int _current = 0x2782;

        public void Dispose()
        {
            foreach (var lifetime in _global.ToArray())
            {
                lifetime.Value.Dispose();
            }
        }

        public LifetimeHelper Create()
            => GetOrCreate(Interlocked.Increment(ref _current));

        public bool TryGet(nint handle, [NotNullWhen(true)] out LifetimeHelper? lifetime)
            => _global.TryGetValue(handle, out lifetime);

        public LifetimeHelper GetOrCreate(nint handle)
            => _global.GetOrAdd(handle, static (handle, manager) => new(manager, handle), this);
    }

    public sealed class OnGC(Action onGC)
    {
        private Action _onGC = onGC;

        ~OnGC()
        {
            _onGC();
        }
    }
}
