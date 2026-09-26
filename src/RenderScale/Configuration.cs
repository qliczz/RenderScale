using Dalamud.Configuration;
using Newtonsoft.Json;

namespace RenderScale;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public float Percent { get; set; } = 100;
    // Only the preferred number is persisted; loading never activates native overrides.
    [JsonIgnore] public ConfigurationV1 Runtime = new();
    [JsonIgnore] public ref ConfigurationV1 _ => ref Runtime;
}

public struct ConfigurationV1
{
    public SizeConfig GameTarget = new();
    public float GameRenderScale = 1;
    public ResolutionScalingMode ResolutionScalingMode = ResolutionScalingMode.FSR;
    public ConfigurationV1() { }
    public struct SizeConfig
    {
        public const float MinScale = .25f;
        public bool IsEnabled;
        public bool IsScale = true;
        public float Scale = 1;
        public uint Width = 1024;
        public uint Height = 1024;
        public SizeConfig() { }
    }
}

public sealed class DebugConfiguration { public bool IsDebug => false; }

// Mappings follow CustomResolution's runtime vs saved-config distinction.
public enum ResolutionScalingMode { Point, Linear, FSR, DLSS }
public static class ResolutionScalingModeExt
{
    public static ResolutionScalingMode FromXIVSysConf(uint mode) => mode switch
    { 0 => ResolutionScalingMode.FSR, 1 => ResolutionScalingMode.DLSS, _ => ResolutionScalingMode.Linear };
    public static byte ToXIVGFX(this ResolutionScalingMode mode) => mode switch
    { ResolutionScalingMode.FSR => 1, ResolutionScalingMode.DLSS => 2, _ => 0 };
    public static ResolutionScalingMode ToSupported(this ResolutionScalingMode mode) => mode;
}
