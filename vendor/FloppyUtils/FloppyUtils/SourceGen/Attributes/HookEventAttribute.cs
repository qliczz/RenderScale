using System;

namespace FloppyUtils.SourceGen.Attributes;

/// <summary>
/// Generates an event and the necessary boilerplate for listeners to call the original / previous listener.
/// </summary>
/// <remarks>
/// Make sure to call the auto-generated Init{Name}Event after the field has been set.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class HookEventAttribute : Attribute
{
    public string? Lock { get; set; }
    public string? RLock { get; set; }
    public string? WLock { get; set; }
}
