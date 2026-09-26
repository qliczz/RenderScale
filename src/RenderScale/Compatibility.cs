using System.Diagnostics;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace RenderScale;

internal static class Compatibility
{
    public const string GameVersion = "2026.09.15.0000.0000";
    public const string GameHash = "7BA28760BC53CBBBF66B63105FEA6C1BFE09CB6B1C6353FC8FB0FF03CC1A30F4";
    public const string StructsHash = "D397764C6DD787C865E7422EA12C0C4DFF1AD5B5117F4047F6E033ECE5E84E77";
    internal static string? Check()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null || !string.Equals(Path.GetFileName(exe), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                return "当前进程不是 FF14 DX11。";
            if (Hash(exe) != GameHash) return $"客户端尚未核对。此测试版仅面向国服 {GameVersion}。";
            if (Hash(typeof(GraphicsConfig).Assembly.Location) != StructsHash)
                return "卫月结构库已变化，需要重新核对后才能启用。";
            return null;
        }
        catch (Exception ex) { return $"无法完成兼容性检查：{ex.GetType().Name}"; }
    }
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
