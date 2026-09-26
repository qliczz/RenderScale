using FloppyUtils.SourceGen.Ext.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using static FloppyUtils.SourceGen.Helpers.DiagnosticInfo;

namespace FloppyUtils.SourceGen.Helpers;

internal record struct DiagnosticInfo(
    DiagnosticDescriptor Desc,
    LocationInfo? Loc,
    EquatableArray<string> Args
)
{
    public readonly Diagnostic Create() => Diagnostic.Create(Desc, Loc?.Create(), Args.Cast<object>().ToArray());

    internal record struct LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
    {
        public readonly Location Create() => Location.Create(FilePath, TextSpan, LineSpan);
    }
}

internal static class DiagnosticInfoExtensions
{
    public static DiagnosticInfo ToInfo(this DiagnosticDescriptor desc, LocationInfo? location, params string[] args)
    {
        return new(desc, location, args.ToImmutableArray());
    }

    public static LocationInfo? ToInfo(this Location? location)
    {
        if (location?.SourceTree is null)
        {
            return null;
        }

        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }
}
