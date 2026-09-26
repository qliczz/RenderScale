using FloppyUtils.Ptrs;
using SharpDX.D3DCompiler;
using System;
using System.IO;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using XIVDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace FloppyUtils.Graphics;

/// <summary>
/// Minimal HLSL shader / rendering utilities.
/// Expects the shader code to exist as an embedded resource containing HLSL source code
/// on a path matching the type name, unless a path to a separate embedded resource is given.
/// </summary>
public unsafe class MiniShader : IDisposable
{
    public ComPtr<ID3D11DeviceContext> Ctx;
    public ComPtr<ID3D11Device> Device;

    public ComPtr<ID3D11VertexShader> VS;
    public ComPtr<ID3D11PixelShader> PS;

    protected MiniShader(string path = "")
    {
        var type = GetType();
        if (string.IsNullOrEmpty(path))
        {
            path = type.FullName!;

            if (path.EndsWith("Shader"))
            {
                path = path[..^"Shader".Length];
            }

            path += ".hlsl";
        }

        Path = path;

        using (var stream = type.Assembly.GetManifestResourceStream(path) ?? throw new FileNotFoundException(path))
        using (var streamReader = new StreamReader(stream))
        {
            Source = streamReader.ReadToEnd();
        }

        var dev = (DeviceEx*) XIVDevice.Instance();
        Ctx.Attach(dev->D3D11DeviceContext);

        Ctx.Get()->GetDevice(Ptr.Out(out OutComPtr<ID3D11Device> d3ddev));
        Device.Attach(d3ddev.Value);

        var compiledData = LoadOrCompile("vs");
        fixed (byte* compiledDataPtr = compiledData)
        {
            Device.Get()->CreateVertexShader(compiledDataPtr, (nuint) compiledData.Length, null, Ptr.Out(out OutComPtr<ID3D11VertexShader> vs)).ThrowIfFailed();
            VS.Attach(vs.Value);
        }

        compiledData = LoadOrCompile("ps");
        fixed (byte* compiledDataPtr = compiledData)
        {
            Device.Get()->CreatePixelShader(compiledDataPtr, (nuint) compiledData.Length, null, Ptr.Out(out OutComPtr<ID3D11PixelShader> ps)).ThrowIfFailed();
            PS.Attach(ps.Value);
        }
    }

    public string Path { get; private set; }
    public string Source { get; private set; }

    public virtual void Dispose()
    {
        PS.Dispose();
        VS.Dispose();
        Device.Dispose();
    }

    private byte[] LoadOrCompile(string type)
    {
        var path = System.IO.Path.GetFileNameWithoutExtension(Path) + $"_{type}.dxbc";
        using (var stream = GetType().Assembly.GetManifestResourceStream(path))
        {
            if (stream is not null)
            {
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                return bytes;
            }
        }

        var compiled = ShaderBytecode.Compile(Source, type, $"{type}_5_0");
        if (compiled.HasErrors)
        {
            throw new Exception($"Shader compilation failed for {path}: {compiled.Message}");
        }

#if DEBUG
        var tmp = System.IO.Path.Join(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(path));
        File.WriteAllBytes(tmp, compiled.Bytecode.Data);
        FService.PluginLog.Warning("Hey, <plugin developer name here>! You forgot to bundle some precompiled shader blobs!");
        FService.PluginLog.Warning($"{path} compiled to {tmp}, please copy it into your plugin as an embedded resource!");
#endif

        if (!string.IsNullOrEmpty(compiled.Message))
        {
            FService.PluginLog.Debug(compiled.Message);
        }

        return compiled.Bytecode.Data;
    }
}

/// <summary>
/// <see cref="MiniShader"/> with a constant buffer struct attached to it for simplicity.
/// </summary>
/// <typeparam name="TConstants"></typeparam>
public class MiniShader<TConstants> : MiniShader where TConstants : unmanaged
{
    protected MiniShader(string path = "")
        : base(path)
    {
        Constants = new(this);
    }

    public MiniShaderConstants<TConstants> Constants { get; private set; }

    public override void Dispose()
    {
        Constants.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Constant buffer to be used with <see cref="MiniShader"/>.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed unsafe class MiniShaderConstants<T> : IDisposable where T : unmanaged
{
    private static bool _logged = false;
    private readonly MiniShader _shader;
    
    public ComPtr<ID3D11Buffer> Buffer;
    public T Value;

    public MiniShaderConstants(MiniShader shader)
    {
        _shader = shader;

        var stride = Marshal.SizeOf<T>();
        var size = (stride + 15) & ~15;

        if (!_logged)
        {
            FService.PluginLog.Debug($"MiniShaderConstants<{typeof(T)}> size {size} stride {stride}");
            _logged = true;
        }

        D3D11_BUFFER_DESC desc = new()
        {
            ByteWidth = (uint) size,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint) D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = 0,
            MiscFlags = 0,
            StructureByteStride = (uint) stride
        };

        _shader.Device.Get()->CreateBuffer(&desc, null, Ptr.Out(out OutComPtr<ID3D11Buffer> buffer)).ThrowIfFailed();
        Buffer.Attach(buffer.Value);

        Update();
    }

    public void Dispose()
    {
        Buffer.Dispose();
    }

    public void Update()
    {
        Set(Value);
    }

    public void Set(T data)
    {
        Value = data;
        _shader.Ctx.Get()->UpdateSubresource(SafeCast.From(Buffer.Get()).ToBase<ID3D11Resource>(), 0, null, &data, 0, 0);
    }
}
