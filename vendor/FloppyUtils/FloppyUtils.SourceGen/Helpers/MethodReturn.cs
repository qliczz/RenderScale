using FloppyUtils.SourceGen.Ext.Extensions;
using FloppyUtils.SourceGen.Ext.Helpers;
using Microsoft.CodeAnalysis;

namespace FloppyUtils.SourceGen.Helpers;

internal record struct MethodReturn(
    bool IsVoid,
    string Type
);

internal static class MethodReturnExtensions
{
    public static MethodReturn GetMethodReturn(this IMethodSymbol symbol)
    {
        return new(symbol.ReturnsVoid, symbol.ReturnType.GetNameWithContainingTypeAndNamespace());
    }

    public static void WriteSigReturnType(this IndentedTextWriter writer, in MethodReturn info)
    {
        if (info.IsVoid)
        {
            writer.Write("void");
        }
        else
        {
            writer.Write(info.Type);
        }
    }

    public static void WriteReturnValue(this IndentedTextWriter writer, in MethodReturn info)
    {
        if (!info.IsVoid)
        {
            writer.Write("return ");
        }
    }

    public static void WriteLineReturnVoid(this IndentedTextWriter writer, in MethodReturn info)
    {
        if (info.IsVoid)
        {
            writer.WriteLine("return;");
        }
    }
}
