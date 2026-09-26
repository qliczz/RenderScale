using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.Windows;

namespace FloppyUtils.Ptrs;

public static unsafe partial class Ptr
{
    /// <summary>
    /// Syntax sugar to turn T* params into out params.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="at"></param>
    /// <returns></returns>
    public static OutPtr<T>.P Out<T>(out OutPtr<T> at) where T : unmanaged
    {
        at = new();
#pragma warning disable CS9088 // This returns a parameter by reference but it is scoped to the current method
        return new((T*) Unsafe.AsPointer(ref at));
#pragma warning restore CS9088
    }

    /// <summary>
    /// Syntax sugar to turn T** params into out params.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="at"></param>
    /// <returns></returns>
    public static OutPtrPtr<T>.P Out<T>(out OutPtrPtr<T> at) where T : unmanaged
    {
        at = new();
#pragma warning disable CS9088 // This returns a parameter by reference but it is scoped to the current method
        return new((T**) Unsafe.AsPointer(ref at));
#pragma warning restore CS9088
    }

    /// <summary>
    /// Syntax sugar to turn COM T** params into out params.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="at"></param>
    /// <returns></returns>
    public static OutComPtr<T>.P Out<T>(out OutComPtr<T> at) where T : unmanaged, IUnknown.Interface
    {
        at = new();
#pragma warning disable CS9088 // This returns a parameter by reference but it is scoped to the current method
        return new((ComPtr<T>*) Unsafe.AsPointer(ref at));
#pragma warning restore CS9088
    }
}

/// <summary>
/// Ugly hack to allow inline out var declarations in pointer contexts.
/// </summary>
/// <typeparam name="T"></typeparam>
[StructLayout(LayoutKind.Sequential)]
public unsafe ref struct OutPtr<T> where T : unmanaged
{
    public T Value;

    public readonly ref struct P(T* ptr)
    {
        public readonly T* Value = ptr;
        public static implicit operator T*(in P p) => p.Value;
        public static implicit operator void*(in P p) => p.Value;
    }
}

/// <summary>
/// Ugly hack to allow inline out var declarations in pointer contexts.
/// </summary>
/// <typeparam name="T"></typeparam>
[StructLayout(LayoutKind.Sequential)]
public unsafe ref struct OutPtrPtr<T> where T : unmanaged
{
    public Ptr<T> Value;

    public readonly ref struct P(T** ptr)
    {
        public readonly T** Value = ptr;
        public static implicit operator T**(in P p) => p.Value;
        public static implicit operator void**(in P p) => (void**) p.Value;
    }
}

/// <summary>
/// Ugly hack to allow inline out var declarations in pointer contexts.
/// </summary>
/// <typeparam name="T"></typeparam>
[StructLayout(LayoutKind.Sequential)]
public ref struct OutComPtr<T> where T : unmanaged, IUnknown.Interface
{
    public ComPtr<T> Value;

    public readonly unsafe ref struct P(ComPtr<T>* ptr)
    {
        public readonly ComPtr<T>* Value = ptr;
        public static implicit operator T**(in P p) => (T**) p.Value;
        public static implicit operator void**(in P p) => (void**) p.Value;
        public static implicit operator ComPtr<T>*(in P p) => p.Value;
    }
}
