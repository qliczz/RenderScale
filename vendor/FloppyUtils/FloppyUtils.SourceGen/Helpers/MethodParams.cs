using FloppyUtils.SourceGen.Ext.Extensions;
using FloppyUtils.SourceGen.Ext.Helpers;
using Microsoft.CodeAnalysis;

namespace FloppyUtils.SourceGen.Helpers;

internal record struct MethodParam(
    string Type,
    string Name
);

internal static class MethodParamsExtensions
{
    public static EquatableArray<MethodParam> GetMethodParams(this IMethodSymbol symbol)
    {
        using var args = new ImmutableArrayBuilder<MethodParam>();
        foreach (var param in symbol.Parameters)
        {
            args.Add(new(param.Type.GetNameWithContainingTypeAndNamespace(), param.Name));
        }

        return args.ToImmutable();
    }

    public static void WriteSigParam(this IndentedTextWriter writer, in MethodParam info)
    {
        writer.Write(info.Type);
        writer.Write(" ");
        writer.Write(info.Name);
     }

    public static void WriteCallParam(this IndentedTextWriter writer, in MethodParam info)
    {
        writer.Write(info.Name);
    }

    public static void WriteSigParams(this IndentedTextWriter writer, in EquatableArray<MethodParam> info)
    {
        for (int i = 0; i < info.Length - 1; i++)
        {
            writer.WriteSigParam(info[i]);
            writer.Write(", ");
        }

        for (int i = info.Length - 1; 0 <= i && i < info.Length; i++)
        {
            writer.WriteSigParam(info[i]);
        }
    }

    public static void WriteCallParams(this IndentedTextWriter writer, in EquatableArray<MethodParam> info)
    {
        for (int i = 0; i < info.Length - 1; i++)
        {
            writer.WriteCallParam(info[i]);
            writer.Write(", ");
        }

        for (int i = info.Length - 1; 0 <= i && i < info.Length; i++)
        {
            writer.WriteCallParam(info[i]);
        }
    }
}
