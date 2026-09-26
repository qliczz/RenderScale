using TerraFX.Interop.DirectX;

namespace FloppyUtils.Graphics;

public static class D3D11ToStringExtensions
{
    public static string ToDebugString(this D3D11_TEXTURE2D_DESC desc)
        => $"{desc.Width} x {desc.Height} * {desc.MipLevels} [{desc.ArraySize}] @ {desc.Format}, x{desc.SampleDesc.Count} samples @ quality {desc.SampleDesc.Quality}, bind 0x{desc.BindFlags:X8} cpu 0x{desc.CPUAccessFlags:X8} misc 0x{desc.MiscFlags:X8}";
}
