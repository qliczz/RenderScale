using System;
using System.Globalization;
using System.Text;

namespace FloppyUtils;

/// <summary>
/// Utility to parse the bytes out of Ghidra formatted code,
/// allowing for fancy commentated signatures because I cannot be arsed
/// to re-find the same thing a thousand times, let alone forget what sig's what. -jade
/// </summary>
public readonly struct GhidraCode
{
    public readonly string Full;
    public readonly string Sig;

    public readonly string Clean => Sig.Replace("|", "");

    public GhidraCode(string text)
    {
        Full = text = text.Trim() + "\n";
        var all = text.AsSpan();

        var sig = new StringBuilder();
        bool first = true;

        foreach (var lineRange in all.Split('\n'))
        {
            var line = all[lineRange].Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            foreach (var range in line.Split(' '))
            {
                var part = line[range].Trim();
                if (part.IsEmpty)
                {
                    continue;
                }

                // Check for address.
                if (part.Length > 3 && long.TryParse(part, NumberStyles.HexNumber, null, out _))
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        sig.Append("|");
                    }
                    continue;
                }

                // Instruction name or comment - abort.
                if (char.IsUpper(part[0]) || char.IsSymbol(part[0]))
                {
                    break;
                }

                sig.Append(' ');
                sig.Append(part);
            }
        }

        Sig = sig.ToString().TrimStart();
    }
}
