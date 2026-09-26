// From https://github.com/aers/FFXIVClientStructs/tree/main/InteropGenerator/Extensions

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FloppyUtils.SourceGen.Ext.Extensions;

/// <summary>
///     Extension methods for <see cref="MemberDeclarationSyntax" /> types.
/// </summary>
internal static class MemberDeclarationSyntaxExtensions {
    public static bool HasModifier(this MemberDeclarationSyntax memberDeclaration, SyntaxKind modifierKind) {
        return memberDeclaration.Modifiers.Any(modifier => modifier.IsKind(modifierKind));
    }
}
