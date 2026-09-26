// From https://github.com/aers/FFXIVClientStructs/tree/main/InteropGenerator/Extensions

using Microsoft.CodeAnalysis;

namespace FloppyUtils.SourceGen.Ext.Extensions;

public static class RefKindExtensions {
    public static string GetStringPrefix(this RefKind refKind) {
        switch (refKind) {
            case RefKind.In:
                return "in ";
            case RefKind.Out:
                return "out ";
            case RefKind.Ref:
                return "ref ";
            case RefKind.RefReadOnlyParameter:
                return "ref readonly ";
            default:
                return "";
        }
    }
}
