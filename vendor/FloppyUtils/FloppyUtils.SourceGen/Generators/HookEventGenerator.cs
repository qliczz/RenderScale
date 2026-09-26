using FloppyUtils.SourceGen.Ext.Extensions;
using FloppyUtils.SourceGen.Ext.Helpers;
using FloppyUtils.SourceGen.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FloppyUtils.SourceGen.Generators;

[Generator]
public sealed class HookEventGenerator : IIncrementalGenerator
{
    internal const string Attribute = "FloppyUtils.SourceGen.Attributes.HookEventAttribute";

    private static readonly DiagnosticDescriptor ErrInvalidName = new(
        $"FloppyUtils.SourceGen.HookEvent.{nameof(ErrInvalidName)}",
        "Invalid hook event field name",
        "The field name resolves to an empty wanted name",
        "", DiagnosticSeverity.Error, true
    );

    private static readonly DiagnosticDescriptor ErrInvalidType = new(
        $"FloppyUtils.SourceGen.HookEvent.{nameof(ErrInvalidType)}",
        "Invalid hook event field type",
        "The field type does not resolve to a hook type: {0}",
        "", DiagnosticSeverity.Error, true
    );

    private static readonly DiagnosticDescriptor ErrInvalidLock = new(
        $"FloppyUtils.SourceGen.HookEvent.{nameof(ErrInvalidLock)}",
        "Invalid lock setup",
        "Only one lock is supported at any given time",
        "", DiagnosticSeverity.Error, true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var infos = context.SyntaxProvider.ForAttributeWithMetadataName(
            Attribute,
            static (node, _) => node is VariableDeclaratorSyntax,
            static (context, token) => context.TargetSymbol is IFieldSymbol symbol ? Parse(context, symbol, token) : default
        ).Where(i => i != default);

        context.RegisterSourceOutput(
            infos,
            static (context, item) =>
            {
                if (item.Item1 is { } diag)
                {
                    context.ReportDiagnostic(diag.Create());
                }

                if (item.Item2 is { } info)
                {
                    context.AddSource($"{info.Declaring.FullName}.{info.WantedName}.HookEvent.g.cs", Generate(info, context.CancellationToken));
                }

                if (item.Item3 is { } scoped)
                {
                    context.AddSource($"{scoped.Hierarchy.FullName}.{scoped.Events[0].Name}.ScopedHookEvent.g.cs", ScopedEventsGenerator.Generate(scoped, false, context.CancellationToken));
                }
            }
        );
    }

    internal static (DiagnosticInfo?, Info?, ScopedEventsGenerator.Info?) Parse(in GeneratorAttributeSyntaxContext context, IFieldSymbol symbol, CancellationToken token)
    {
        Span<char> name = stackalloc char[symbol.Name.Length];
        symbol.Name.AsSpan().CopyTo(name);
        if (name.StartsWith("_"))
        {
            name = name[1..];
        }
        if (name.EndsWith("Hook"))
        {
            name = name[..^"Hook".Length];
        }
        if (name.Length == 0)
        {
            return (
                ErrInvalidName.ToInfo(context.TargetNode.GetLocation().ToInfo()),
                null,
                null
            );
        }
        if (char.IsLower(name[0]))
        {
            name[0] = char.ToUpperInvariant(name[0]);
        }

        if (symbol.Type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } hookType)
        {
            return (
                ErrInvalidType.ToInfo(context.TargetNode.GetLocation().ToInfo(), "The field type is not a generic type with one argument."),
                null,
                null
            );
        }

        if (hookType.TypeArguments[0] is not INamedTypeSymbol hookDelegateType)
        {
            return (
                ErrInvalidType.ToInfo(context.TargetNode.GetLocation().ToInfo(), "The field hook type argument is not a named type symbol."),
                null,
                null
            );
        }

        if (hookDelegateType.DelegateInvokeMethod is not { } hookInvoke)
        {
            return (
                ErrInvalidType.ToInfo(context.TargetNode.GetLocation().ToInfo(), "The field hook type argument does not have an invoke method."),
                null,
                null
            );
        }

        var attrib = context.Attributes[0];

        Info info = new(
            symbol.ContainingType.GetHierarchy(),
            symbol.Name,
            name.ToString(),
            "_on" + name.ToString(),
            "_manual" + name.ToString(),
            name.ToString() + "Context",
            name.ToString() + "Event",
            "On" + name.ToString(),
            hookDelegateType.GetNameWithContainingTypeAndNamespace(),
            hookInvoke.GetMethodReturn(),
            hookInvoke.GetMethodParams(),
            attrib.NamedArguments.FirstOrDefault(kvp => kvp.Key.EndsWith("Lock")) is { } lockArg ? (lockArg.Key, lockArg.Value.Value?.ToString()) : ("", null)
        );

        ScopedEventsGenerator.Info? scoped = null;
        if (symbol.ContainingType.TryGetAttributeWithFullyQualifiedMetadataName(ScopedEventsGenerator.Attribute, out _))
        {
            using var events = new ImmutableArrayBuilder<(string, string)>();
            events.Add((info.EventTypeName, info.EventName));
            scoped = new(
                info.Declaring,
                events.ToImmutable()
            );
        }

        return (
            null,
            info,
            scoped
        );
    }

    internal static string Generate(Info info, CancellationToken token)
    {
        using var writer = new IndentedTextWriter();
        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine("#nullable enable");

        using (writer.BeginHierarchy(info.Declaring))
        {
#if EXAMPLE
            public readonly struct ResizeDestroyContext(Delegate[] list, int depth)
            {
                public byte Invoke() => ((ResizeDestroyEvent) list[^depth])(new(list, depth + 1));
            }
#endif

            writer.Write("public readonly struct ");
            writer.Write(info.ContextName);
            writer.WriteLine("(global::System.Delegate[] list, int depth)");
            writer.WriteLine("{");
            writer.IncreaseIndent();

            writer.Write("public ");
            writer.WriteSigReturnType(info.HookReturn);
            writer.Write(" Invoke(");
            writer.WriteSigParams(info.HookParams);
            writer.Write(") => ((");
            writer.Write(info.EventTypeName);
            writer.Write(") list[^depth])(new(list, depth + 1)");
            if (!info.HookParams.IsEmpty)
            {
                writer.Write(", ");
                writer.WriteCallParams(info.HookParams);
            }
            writer.WriteLine(");");

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();

#if EXAMPLE
            public delegate byte ResizeDestroyEvent(ResizeDestroyContext ctx);
            private ResizeDestroyEvent _onResizeDestroy;
            public event ResizeDestroyEvent OnResizeDestroy
            {
                add
                {
                    _onResizeDestroy += value;
                    _resizeDestroyHook?.Enable();
                }
                remove
                {
#pragma warning disable CS8601 // Field should never revert to null via removal
                    _onResizeDestroy -= value;
#pragma warning restore CS8601
                    if (_resizeDestroyHook is not null && _onResizeCreate!.HasSingleTarget)
                    {
                        _resizeDestroyHook.Disable();
                    }
                }
            }
#endif

            writer.Write("public delegate ");
            writer.WriteSigReturnType(info.HookReturn);
            writer.Write(" ");
            writer.Write(info.EventTypeName);
            writer.Write("(");
            writer.Write(info.ContextName);
            writer.Write(" ctx");
            if (!info.HookParams.IsEmpty)
            {
                writer.Write(", ");
                writer.WriteSigParams(info.HookParams);
            }
            writer.WriteLine(");");

            writer.Write("private ");
            writer.Write(info.EventTypeName);
            writer.Write(" ");
            writer.Write(info.EventFieldName);
            writer.WriteLine(";");

            writer.Write("public event ");
            writer.Write(info.EventTypeName);
            writer.Write(" ");
            writer.WriteLine(info.EventName);
            writer.WriteLine("{");
            writer.IncreaseIndent();

            writer.WriteLine("add");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.Write(info.EventFieldName);
            writer.WriteLine(" += value;");
            if (info.Lock.Name is not { Length: > 0 })
            {
                writer.Write(info.HookFieldName);
                writer.WriteLine("?.Enable();");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");

            writer.WriteLine("remove");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine("#pragma warning disable CS8601 // Field should never revert to null via removal");
            writer.Write(info.EventFieldName);
            writer.WriteLine(" -= value;");
            writer.WriteLine("#pragma warning restore CS8601");
            if (info.Lock.Name is not { Length: > 0 })
            {
                writer.Write("if (");
                writer.Write(info.HookFieldName);
                writer.Write(" is not null &&");
                writer.Write(info.EventFieldName);
                writer.WriteLine("!.HasSingleTarget)");
                writer.WriteLine("{");
                writer.IncreaseIndent();
                writer.Write(info.HookFieldName);
                writer.WriteLine(".Disable();");
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");

            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();

#if EXAMPLE
            private byte OnResizeDestroyDetour()
            {
                lock (Lock)
                {
                    return new ResizeDestroyContext(_onResizeDestroy.GetInvocationList(), 1).Invoke();
                }
            }
#endif

            writer.Write("private ");
            writer.WriteSigReturnType(info.HookReturn);
            writer.Write(" On");
            writer.Write(info.WantedName);
            writer.Write("Detour(");
            writer.WriteSigParams(info.HookParams);
            writer.WriteLine(")");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            if (info.Lock.Name is { Length: > 0 })
            {
                switch (info.Lock.Type)
                {
                    case "Lock":
                    default:
                        writer.Write("lock (");
                        writer.Write(info.Lock.Name);
                        writer.WriteLine(")");
                        break;
                    case "RLock":
                        writer.Write("using (");
                        writer.Write(info.Lock.Name);
                        writer.WriteLine(".EnterReadLock())");
                        break;
                    case "WLock":
                        writer.Write("using (");
                        writer.Write(info.Lock.Name);
                        writer.WriteLine(".EnterWriteLock())");
                        break;
                }
                writer.WriteLine("{");
                writer.IncreaseIndent();
            }
            writer.WriteReturnValue(info.HookReturn);
            writer.Write("new ");
            writer.Write(info.ContextName);
            writer.Write("(");
            writer.Write(info.EventFieldName);
            writer.Write(".GetInvocationList(), 1).Invoke(");
            writer.WriteCallParams(info.HookParams);
            writer.WriteLine(");");
            if (info.Lock.Name is { Length: > 0 })
            {
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();

#if EXAMPLE
            private ResizeDestroy _manualResizeDestroy;
            public void ManualResizeDestroy() => _manualResizeDestroy();
#endif

            writer.Write("private ");
            writer.Write(info.HookDelegateType);
            writer.Write(" ");
            writer.Write(info.ManualFieldName);
            writer.WriteLine(";");
            writer.Write("public ");
            writer.WriteSigReturnType(info.HookReturn);
            writer.Write(" Manual");
            writer.Write(info.WantedName);
            writer.Write("(");
            writer.WriteSigParams(info.HookParams);
            writer.Write(") => ");
            writer.Write(info.ManualFieldName);
            writer.Write("(");
            writer.WriteCallParams(info.HookParams);
            writer.WriteLine(");");
            writer.WriteLine();

#if EXAMPLE
            [global::System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_onResizeDestroy))]
            [global::System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_manualResizeDestroy))]
            private void InitResizeDestroyHookEvent(ResizeDestroyEvent orig)
            {
                _onResizeDestroy = orig;
                _manualResizeDestroy = global::System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ResizeDestroy>(_onResizeDestroyHook.Address);
            }
#endif

            writer.Write("[global::System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(");
            writer.Write(info.EventFieldName);
            writer.WriteLine("))]");
            writer.Write("[global::System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(");
            writer.Write(info.ManualFieldName);
            writer.WriteLine("))]");
            writer.Write("private void Init");
            writer.Write(info.EventTypeName);
            writer.Write("(");
            writer.Write(info.EventTypeName);
            writer.WriteLine(" orig)");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.Write(info.EventFieldName);
            writer.WriteLine(" = orig;");
            writer.Write(info.ManualFieldName);
            writer.Write(" = global::System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<");
            writer.Write(info.HookDelegateType);
            writer.Write(">(");
            writer.Write(info.HookFieldName);
            writer.WriteLine(".Address);");
            if (info.Lock.Name is { Length: > 0 })
            {
                writer.Write(info.HookFieldName);
                writer.WriteLine(".Enable();");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine();
        }

        return writer.ToString();
    }

    internal sealed record class Info(
        Hierarchy Declaring,
        string HookFieldName,
        string WantedName,
        string EventFieldName,
        string ManualFieldName,
        string ContextName,
        string EventTypeName,
        string EventName,
        string HookDelegateType,
        MethodReturn HookReturn,
        EquatableArray<MethodParam> HookParams,
        (string Type, string? Name) Lock
    );
}
