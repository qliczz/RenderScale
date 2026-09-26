using FloppyUtils.SourceGen.Attributes;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FloppyUtils.Ptrs;

public static unsafe partial class Ptr
{
    public static Ptr<T> For<T>(nint ptr) where T : unmanaged => (Ptr<T>) ptr;
    public static Ptr<T> For<T>(T* ptr) where T : unmanaged => (Ptr<T>) ptr;
}

/// <summary>
/// Based on ClientStructs Pointer to work around generics limitations,
/// but using a field instead of a property to allow for &ptr->Value for T**
/// and some other tweaks.
/// Assumes 64-bit pointers.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly unsafe partial struct Ptr<T> : IEquatable<Ptr<T>>, IComparable<Ptr<T>> where T : unmanaged
{
    public readonly T* Value;

    private Ptr(T* p)
    {
        Value = p;
    }

    public static implicit operator T*(Ptr<T> p) => p.Value;
    public static implicit operator Ptr<T>(T* p) => new(p);

    public static explicit operator nint(Ptr<T> p) => (nint) p.Value;
    public static explicit operator Ptr<T>(nint p) => new((T*) p);

    /// <summary>
    /// Attempts to read data from the given address in a slow and semi-safe manner.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="values"></param>
    /// <returns></returns>
    [SpanOneOut]
    public bool TryRead(Span<T> values)
    {
        fixed (T* valuesPtr = &values[0])
        {
            nuint read;
            return ReadProcessMemory(
                (HANDLE) Process.GetCurrentProcess().Handle,
                Value,
                valuesPtr,
                (nuint) (sizeof(T) * values.Length),
                &read
            ) && read == (nuint) (sizeof(T) * values.Length);
        }
    }

    /// <summary>
    /// Attempts to obtain the original on-disk / sigscan copy location for this address.
    /// </summary>
    /// <remarks>
    /// This only works for mapped PE files. MapViewOfFile and similar are not supported.
    /// </remarks>
    /// <returns></returns>
    public OrigAddr<T> ToOrig()
    {
        if (!(FService.MappedModuleCache.GetAt((nint) Value) is { } module))
        {
            return new(Value, null, null, null);
        }

        if (module.BaseAddress != 0)
        {
            var rva = ((nint) Value) - module.BaseAddress;

            if (module.Copy is { } copy)
            {
                return new(Value, module.FileName, rva, copy.Base + rva);
            }
        }

        return new(Value, module.FileName, null, null);
    }

    public override string ToString() => $"Ptr<{typeof(T).FullName}>(0x{(long) (nint) Value:X16})";

    public bool Equals(Ptr<T> other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is Ptr<T> other && Equals(other);
    public override int GetHashCode() => ((nint) Value).GetHashCode();
    public static bool operator ==(Ptr<T> left, Ptr<T> right) => left.Value == right.Value;
    public static bool operator !=(Ptr<T> left, Ptr<T> right) => left.Value != right.Value;

    public int CompareTo(Ptr<T> other) => ((nint) Value).CompareTo((nint) other.Value);
    public static bool operator <(Ptr<T> left, Ptr<T> right) => left.Value < right.Value;
    public static bool operator >(Ptr<T> left, Ptr<T> right) => left.Value > right.Value;
    public static bool operator <=(Ptr<T> left, Ptr<T> right) => left.Value <= right.Value;
    public static bool operator >=(Ptr<T> left, Ptr<T> right) => left.Value >= right.Value;
}
