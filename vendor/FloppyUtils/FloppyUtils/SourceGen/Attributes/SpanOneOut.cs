using System;

namespace FloppyUtils.SourceGen.Attributes;

/// <summary>
/// For a given <code>Func(Span<T> x)</code>, generates <code>Func(out T x) => Func(new(&x, 1));</code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SpanOneOutAttribute : Attribute
{
}
