using FloppyUtils.SourceGen.Ext.Extensions;
using FloppyUtils.SourceGen.Ext.Helpers;
using Microsoft.CodeAnalysis;

namespace FloppyUtils.SourceGen.Helpers;

internal record struct Hierarchy(
    string FullName,
    string Namespace,
    EquatableArray<Hierarchy.Part> Parts
)
{
    public record struct Part(string ClassOrStruct, string Name);
}

internal static class HierarchyExtensions
{
    public static Hierarchy GetHierarchy(this INamedTypeSymbol symbol)
    {
        using ImmutableArrayBuilder<Hierarchy.Part> parts = new();

        for (var parent = symbol; parent is not null; parent = parent.ContainingType)
        {
            parts.Add(new(
                (parent.IsRecord ? "record " : "") + (parent.IsValueType ? "struct" : "class"),
                parent.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            ));
        }

        return new(
            symbol.GetNameWithContainingTypeAndNamespace(),
            symbol.ContainingNamespace.GetNameWithContainingTypeAndNamespace(),
            parts.ToImmutable()
        );
    }

    public static EndHierarchyDisposable BeginHierarchy(
        this IndentedTextWriter writer,
        in Hierarchy info,
        string modifiers = "",
        string bases = ""
    )
    {
        if (info.Namespace.Length > 0)
        {
            writer.WriteLine($"namespace {info.Namespace};");
            writer.WriteLine();
        }

        for (int i = info.Parts.Length - 1; i >= 1; i--)
        {
            writer.WriteLine($"unsafe partial {info.Parts[i].ClassOrStruct} {info.Parts[i].Name}");
            writer.WriteLine("{");
            writer.IncreaseIndent();
        }

        if (info.Parts.Length != 0)
        {
            writer.Write("unsafe ");
            
            if (!string.IsNullOrEmpty(modifiers))
            {
                writer.Write(modifiers);
                writer.Write(" ");
            }

            writer.Write($"partial {info.Parts[0].ClassOrStruct} {info.Parts[0].Name}");

            if (!string.IsNullOrEmpty(bases))
            {
                writer.Write(" : ");
                writer.Write(bases);
            }

            writer.WriteLine();
            writer.WriteLine("{");
            writer.IncreaseIndent();
        }

        return new(writer, in info);
    }

    public static void EndHierarchy(this IndentedTextWriter writer, in Hierarchy hierarchy)
    {
        for (var i = 0; i < hierarchy.Parts.Length; i++)
        {
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
    }

    internal readonly struct EndHierarchyDisposable(IndentedTextWriter writer, ref readonly Hierarchy hierarchy) : IDisposable
    {
        private readonly IndentedTextWriter _writer = writer;
        private readonly Hierarchy _hierarchy = hierarchy;

        public void Dispose()
        {
            _writer.EndHierarchy(_hierarchy);
        }
    }
}
