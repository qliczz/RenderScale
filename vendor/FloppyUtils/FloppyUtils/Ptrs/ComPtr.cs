using System.Runtime.CompilerServices;
using TerraFX.Interop.Windows;

namespace FloppyUtils.Ptrs;

public static unsafe class ComPtr
{
    /// <summary>
    /// <see cref="ComPtr{TA}.Attach(TA*)"/> because ComPtr makes dealing with
    /// non-refcount-increasing scenarios more verbose than it needs to be.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="ptr"></param>
    /// <returns></returns>
    public static ComPtr<T> Attach<T>(T* ptr) where T : unmanaged, IUnknown.Interface
    {
        Unsafe.SkipInit(out ComPtr<T> comPtr);
        *comPtr.GetAddressOf() = ptr;
        return comPtr;
    }

    public static ComPtr<T> AddRef<T>(T* ptr) where T : unmanaged, IUnknown.Interface
    {
        return ptr;
    }

    public static uint? TryAddRef<T>(this ComPtr<T> comptr) where T : unmanaged, IUnknown.Interface
    {
        if (comptr.Get() != null)
        {
            return comptr.Get()->AddRef();
        }

        return null;
    }

    public static uint? TryRelease<T>(this ComPtr<T> comptr) where T : unmanaged, IUnknown.Interface
    {
        if (comptr.Get() != null)
        {
            return comptr.Get()->Release();
        }

        return null;
    }

    // Maybe some day, https://github.com/dotnet/csharplang/discussions/9675 will change. *huffs copium*
    extension<T>(ComPtr<T> comptr) where T : unmanaged, IUnknown.Interface
    {
        public TVtbl* GetVtbl<TVtbl>() where TVtbl : unmanaged
            => (TVtbl*) ((IUnknown*) comptr.Get())->lpVtbl;
    }
}
