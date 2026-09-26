using System;
using System.Runtime.InteropServices;

namespace FloppyUtils.Ptrs;

/// <summary>
/// Heap allocation helper.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="ptr"></param>
/// <param name="free"></param>
public unsafe class Alloc<T>(T* ptr, Action<nint> free) : IDisposable where T : unmanaged
{
    private readonly Action<nint> _free = free;
    private bool _disposed;

    public Alloc(nuint count = 1) : this((T*) NativeMemory.Alloc(((nuint) sizeof(T)) * count), static ptr => NativeMemory.Free((void*) ptr))
    {
    }

    public T* Ptr { get; } = ptr;

    ~Alloc()
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _free((nint) Ptr);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public void Disown()
    {
        _disposed = true;
    }
}
