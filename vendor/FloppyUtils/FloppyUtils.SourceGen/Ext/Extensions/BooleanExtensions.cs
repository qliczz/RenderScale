// From https://github.com/aers/FFXIVClientStructs/tree/main/InteropGenerator/Extensions

namespace FloppyUtils.SourceGen.Ext.Extensions;

public static class BooleanExtensions {
    public static string ToLowercaseString(this bool boolean) => boolean ? "true" : "false";
}
