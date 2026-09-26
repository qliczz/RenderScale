using System;

namespace FloppyUtils.SourceGen.Attributes;

/// <summary>
/// Adds a CreateScope function which returns a scoped proxy.
/// Any event listeners added to the scoped proxy will be removed on its disposal.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScopedEventsAttribute : Attribute
{
}
