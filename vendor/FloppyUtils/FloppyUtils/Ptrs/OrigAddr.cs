using FloppyUtils.Ptrs;
using FloppyUtils.SourceGen.Attributes;
using System;
using System.IO;
using System.Text;

namespace FloppyUtils;

/// <summary>
/// Represents an address mapped to an original PE module file location.
/// </summary>
public readonly unsafe partial record struct OrigAddr<T>(nint Value, string? FileName, long? Position, nint? Copy) where T : unmanaged
{
    public OrigAddr(T* value, string? fileName, long? position, nint? copy)
        : this((nint) value, fileName, position, copy)
    {
    }

    public bool IsValid => Value != default && (Copy.HasValue || (!string.IsNullOrEmpty(FileName) && Position.HasValue));

    public override string ToString()
    {
        var sb = new StringBuilder();

        sb.Append("OrigAddr(0x");
        sb.Append(((long) Value).ToString("X16"));

        if (Copy is { } copy)
        {
            sb.Append("=copy>0x");
            sb.Append(((long) copy).ToString("X16"));
        }

        {
            if (FileName is { Length: > 0 } fileName && Position is { } position)
            {
                sb.Append("=orig>");
                sb.Append(Path.GetFileName(fileName));
                sb.Append('+');
                sb.Append(position.ToString("X"));
            }
        }

        return sb.Append(')').ToString();
    }

    /// <summary>
    /// Attempts to read data from the given address from its original file in a very slow manner.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="values"></param>
    /// <returns></returns>
    [SpanOneOut]
    public bool TryRead(Span<T> values)
    {
        if (Copy is { } copy)
        {
            return Ptr.For<T>(copy).TryRead(values);
        }
        
        // If no copy address is given, assume the position isn't an RVA or anything, but a raw position.
        if (FileName is { Length: > 0 } fileName && Position is { } position)
        {
            var valuesRaw = Ptr.For(values).As<byte>().Span(values.Length * sizeof(T));

            try
            {
                using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(position, SeekOrigin.Begin);
                return fs.Read(valuesRaw) == valuesRaw.Length;
            }
            catch (Exception e)
            {
                // File suddenly doesn't exist? Messed up contents? Locked? ¯\_(ツ)_/¯
                FService.PluginLog.Warning($"OrigAddr @ {Value.ToDebugString()} failed to read from {fileName} @ 0x{position:X}: {e}");
                return false;
            }
        }

        return false;
    }
}
