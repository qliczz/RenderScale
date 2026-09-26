using System;

namespace FloppyUtils.SourceGen.Attributes;

/// <summary>
/// Fills all properties and methods with WeakRoot getters / setters / invokers.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class WeakProxyAttribute : Attribute
{
}
