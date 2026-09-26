using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FloppyUtils.Ptrs;

public static partial class Ptr
{
    public static RefPtr<T> For<T>(ref T at) where T : unmanaged => new(ref at);

    public static RefPtr<T> For<T>(Span<T> ptr) where T : unmanaged => new(ref ptr[0]);
}

/// <summary>
/// I have a ref, I have a pointer. Uh! Ref-pointer!
/// </summary>
/// <typeparam name="TAt"></typeparam>
[StructLayout(LayoutKind.Sequential)]
public unsafe readonly ref struct RefPtr<TAt> : IEquatable<RefPtr<TAt>>, IComparable<RefPtr<TAt>> where TAt : unmanaged
{
    public readonly ref TAt Ref;

    public TAt* Ptr
    {
        get => (TAt*) Unsafe.AsPointer(ref Ref);
    }

    internal RefPtr(ref TAt at)
    {
        Ref = ref at;
    }

    internal RefPtr(TAt* at)
    {
        Ref = ref Unsafe.AsRef<TAt>(at);
    }

    public static implicit operator TAt*(RefPtr<TAt> p) => p.Ptr;
    public static implicit operator RefPtr<TAt>(TAt* p) => new(p);

    public RefPtr<T> As<T>() where T : unmanaged => Offs<T>(0);

    public RefPtr<T> After<T>() where T : unmanaged => Offs<T>(sizeof(TAt));

    public RefPtr<T> Offs<T>(string field) where T : unmanaged => Offs<T>(Marshal.OffsetOf<TAt>(field));

    public RefPtr<T> Offs<T>(int offs) where T : unmanaged => Offs<T>((nint) offs);

    public RefPtr<T> Offs<T>(nint offs) where T : unmanaged => new((T*) ((nint) Ptr + offs));

    public Span<TAt> Span(int length) => new(Ptr, length);

    public override string ToString() => $"RefPtr<{typeof(TAt).FullName}>(0x{(long) (nint) Ptr:X16})";

    public bool Equals(RefPtr<TAt> other) => Ptr == other.Ptr;
    public override bool Equals(object? obj) => false;
    public override int GetHashCode() => ((nint) Ptr).GetHashCode();
    public static bool operator ==(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr == right.Ptr;
    public static bool operator !=(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr != right.Ptr;

    public int CompareTo(RefPtr<TAt> other) => ((nint) Ptr).CompareTo((nint) other.Ptr);
    public static bool operator <(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr < right.Ptr;
    public static bool operator >(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr > right.Ptr;
    public static bool operator <=(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr <= right.Ptr;
    public static bool operator >=(RefPtr<TAt> left, RefPtr<TAt> right) => left.Ptr >= right.Ptr;
}
