using System;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FloppyUtils.Graphics;

/// <summary>
/// Wrapper around <see cref="ID3D11VertexShader"/> / <see cref="ID3D11PixelShader"/> / <see cref="ID3D11GeometryShader"/>
/// and its attached <see cref="ID3D11ClassInstance"/>s.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="shader"></param>
/// <param name="instances"></param>
public struct D3D11Shader<T>(ComPtr<T> shader, ComPtr<ID3D11ClassInstance>[]? instances) where T : unmanaged, IUnknown.Interface
{
    static D3D11Shader()
    {
        FService.Assert(GetShader != 0);
        FService.Assert(SetShader != 0);
        FService.Assert(Slotted<ID3D11Buffer>.Get != 0);
        FService.Assert(Slotted<ID3D11Buffer>.Set != 0);
        FService.Assert(Slotted<ID3D11ShaderResourceView>.Get != 0);
        FService.Assert(Slotted<ID3D11ShaderResourceView>.Set != 0);
    }

    public ComPtr<T> Shader = shader;
    public ComPtr<ID3D11ClassInstance>[]? Instances = instances;

    public void Dispose()
    {
        Shader.Dispose();

        if (Instances is not null)
        {
            foreach (var instance in Instances)
            {
                instance.Dispose();
            }
        }
    }

    public static readonly int GetShader = typeof(ID3D11DeviceContext).GetVtblIndex(GetName(nameof(GetShader)));
    public static readonly int SetShader = typeof(ID3D11DeviceContext).GetVtblIndex(GetName(nameof(SetShader)));

    private static string Prefix =>
        typeof(T) == typeof(ID3D11VertexShader) ? "VS" :
        typeof(T) == typeof(ID3D11PixelShader) ? "PS" :
        typeof(T) == typeof(ID3D11GeometryShader) ? "GS" :
        throw new NotSupportedException($"Unsupported type {typeof(T).FullName}");

    private static string GetName(string name) => Prefix + name;

    public static class Slotted<TSlotted> where TSlotted : unmanaged, IUnknown.Interface
    {
        public static readonly int Get = typeof(ID3D11DeviceContext).GetVtblIndex(GetName(nameof(Get)));
        public static readonly int Set = typeof(ID3D11DeviceContext).GetVtblIndex(GetName(nameof(Set)));

        private static string Suffix =>
            typeof(TSlotted) == typeof(ID3D11Buffer) ? "ConstantBuffers" :
            typeof(TSlotted) == typeof(ID3D11ShaderResourceView) ? "ShaderResources" :
            throw new NotSupportedException($"Unsupported type {typeof(TSlotted).FullName}");

        private static string GetName(string name) => Prefix + name + Suffix;
    }
}

/// <summary>
/// Utilities to make dealing with D3D11 via TerraFX a bit more bearable.
/// </summary>
public unsafe static class DXGIShaderExt
{
    public const int D3D11_COMMONSHADER_CONSTANT_BUFFER_API_SLOT_COUNT = 14;

    public delegate void StageGetShader<T>(T** shader, ID3D11ClassInstance** instances, uint* instancesCount) where T : unmanaged, IUnknown.Interface;

    /// <summary>
    /// Gets the currently bound shader of the given type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="ctx"></param>
    /// <returns></returns>
    public static D3D11Shader<T> GetShader<T>(this ComPtr<ID3D11DeviceContext> ctx) where T : unmanaged, IUnknown.Interface
    {
        var fn = (delegate* unmanaged<ID3D11DeviceContext*, T**, ID3D11ClassInstance**, uint*, void>) ctx.Get()->lpVtbl[D3D11Shader<T>.GetShader];

        ComPtr<T> old = new();
        uint count;
        fn(ctx, old.ReleaseAndGetAddressOf(), null, &count);
        if (count == 0)
        {
            return new(old, null);
        }

        Span<ComPtr<ID3D11ClassInstance>> instances = stackalloc ComPtr<ID3D11ClassInstance>[(int) count];
        fn(ctx, old.ReleaseAndGetAddressOf(), instances[0].GetAddressOf(), &count);
        return new(old, instances.ToArray());
    }

    /// <summary>
    /// Sets the currently bound shader of the given type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="ctx"></param>
    /// <param name="shader"></param>
    public static void SetShader<T>(this ComPtr<ID3D11DeviceContext> ctx, in D3D11Shader<T> shader) where T : unmanaged, IUnknown.Interface
    {
        var fn = (delegate* unmanaged<ID3D11DeviceContext*, T*, ID3D11ClassInstance**, uint, void>) ctx.Get()->lpVtbl[D3D11Shader<T>.SetShader];

        if (shader.Instances != null)
        {
            fixed (ComPtr<ID3D11ClassInstance>* instancesPtr = &shader.Instances[0])
            {
                fn(ctx, shader.Shader, instancesPtr[0].GetAddressOf(), (uint) shader.Instances.Length);
            }
        }
        else
        {
            fn(ctx, shader.Shader, null, 0);
        }
    }

    /// <summary>
    /// Gets the slotted resource (SRVs, CBs) of the given type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TSlotted"></typeparam>
    /// <param name="ctx"></param>
    /// <param name="offs"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    public static ComPtr<TSlotted>[] GetSlotted<T, TSlotted>(
        this ComPtr<ID3D11DeviceContext> ctx,
        int offs,
        int count
    ) where T : unmanaged, IUnknown.Interface where TSlotted : unmanaged, IUnknown.Interface
    {
        var fn = (delegate* unmanaged<ID3D11DeviceContext*, uint, uint, TSlotted**, void>) ctx.Get()->lpVtbl[D3D11Shader<T>.Slotted<TSlotted>.Get];

        Span<ComPtr<TSlotted>> vals = stackalloc ComPtr<TSlotted>[count];
        fn(ctx, (uint) offs, (uint) count, vals[0].GetAddressOf());

        return vals.ToArray();
    }

    /// <summary>
    /// Sets the slotted resource (SRVs, CBs) of the given type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TSlotted"></typeparam>
    /// <param name="ctx"></param>
    /// <param name="offs"></param>
    /// <param name="vals"></param>
    public static void SetSlotted<T, TSlotted>(
        this ComPtr<ID3D11DeviceContext> ctx,
        int offs,
        Span<ComPtr<TSlotted>> vals
    ) where T : unmanaged, IUnknown.Interface where TSlotted : unmanaged, IUnknown.Interface
    {
        var fn = (delegate* unmanaged<ID3D11DeviceContext*, uint, uint, TSlotted**, void>) ctx.Get()->lpVtbl[D3D11Shader<T>.Slotted<TSlotted>.Set];

        fixed (ComPtr<TSlotted>* valsPtr = vals)
        {
            fn(ctx, (uint) offs, (uint) vals.Length, valsPtr[0].GetAddressOf());
        }
    }
}
