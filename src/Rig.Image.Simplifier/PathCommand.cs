namespace Rig.Image.Simplifier;

/// <summary>A single SVG path command with its numeric arguments.</summary>
public sealed record PathCommand(char Command, IReadOnlyList<double> Args)
{
    /// <summary>The command written as clean path syntax: arc flags as integers, every other number rounded to <paramref name="precision"/> places with trailing zeros cut.</summary>
    public string Serialize(int precision = 2)
    {
        if (Args.Count == 0)
        {
            return Command.ToString();
        }

        var isArc = Command is 'A' or 'a';
        var formatted = new string[Args.Count];
        for (var index = 0; index < Args.Count; index++)
        {
            if (isArc && index is 3 or 4)
            {
                formatted[index] = PythonNumber.Truncate(Args[index]);
                continue;
            }

            // image_drift's own rstrip chain, quirk included: at precision 0 the zeros of "100" go too.
            var text = PythonNumber.FormatFixed(Args[index], precision).TrimEnd('0').TrimEnd('.');
            formatted[index] = text == "-0" ? "0" : text;
        }

        return $"{Command} {string.Join(' ', formatted)}";
    }
}

/// <summary>A contiguous subpath that starts at an M or m and ends at a Z or z, or where the contour ends.</summary>
public sealed record SubPath(IReadOnlyList<PathCommand> Commands, bool IsClosed);
