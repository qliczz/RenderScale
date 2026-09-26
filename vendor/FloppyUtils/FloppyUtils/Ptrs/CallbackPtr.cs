using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace FloppyUtils.Ptrs;

/// <summary>
/// Strongly typed function type pointer, allowing for struct fields with standard delegate types.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="Value"></param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct CallbackPtr<T>(nint Value) where T : Delegate
{
    public T? ToDelegate()
    {
        return Value == default ? null : Marshal.GetDelegateForFunctionPointer<T>(Value);
    }

    /// <summary>
    /// Cached version of <see cref="CallbackPtr{T}"/>, allowing for strongly typed managed + native pairings.
    /// </summary>
    public readonly struct Cached
    {
        private readonly T? _managed;
        private readonly nint _native;

        public Cached(T? managed)
        {
            _managed = managed;
            _native = managed is null ? default : Marshal.GetFunctionPointerForDelegate(managed);
        }

        public Cached(nint native)
        {
            _native = native;
            _managed = native == default ? null : Marshal.GetDelegateForFunctionPointer<T>(native);
        }

        public T? Managed => _managed;
        public nint Native => _native;

        [MemberNotNullWhen(true, nameof(Managed))]
        public bool IsValid => _native != default;
    }
}
