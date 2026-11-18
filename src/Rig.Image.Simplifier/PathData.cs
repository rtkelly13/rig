using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

/// <summary>The SVG path <c>d</c> tokenizer, parser and subpath splitter, with image_drift's quirks kept.</summary>
public static partial class PathData
{
    private static readonly Dictionary<char, int> Arity = new()
    {
        ['M'] = 2,
        ['m'] = 2,
        ['L'] = 2,
        ['l'] = 2,
        ['H'] = 1,
        ['h'] = 1,
        ['V'] = 1,
        ['v'] = 1,
        ['C'] = 6,
        ['c'] = 6,
        ['S'] = 4,
        ['s'] = 4,
        ['Q'] = 4,
        ['q'] = 4,
        ['T'] = 2,
        ['t'] = 2,
        ['A'] = 7,
        ['a'] = 7,
        ['Z'] = 0,
        ['z'] = 0,
    };

    /// <summary>Parses raw path data. Characters that are neither a command nor a number are skipped, a leading number means M, repeated M pairs become L, and an incomplete argument group is dropped.</summary>
    public static IReadOnlyList<PathCommand> Parse(string d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return [];
        }

        var commands = new List<PathCommand>();
        char? current = null;
        var arguments = new List<double>();
        foreach (Match match in TokenPattern().Matches(d))
        {
            if (match.Groups["cmd"].Success)
            {
                if (current is { } flushed)
                {
                    commands.AddRange(Split(flushed, arguments));
                }

                current = match.Groups["cmd"].Value[0];
                arguments = [];
                if (current is 'Z' or 'z')
                {
                    commands.Add(new PathCommand(current.Value, []));
                    current = null;
                }
            }
            else
            {
                current ??= 'M';
                arguments.Add(double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture));
            }
        }

        if (current is { } last)
        {
            commands.AddRange(Split(last, arguments));
        }

        return commands;
    }

    /// <summary>Splits path data into subpaths at each M or m.</summary>
    public static IReadOnlyList<SubPath> ExtractSubpaths(string d)
    {
        var all = Parse(d);
        if (all.Count == 0)
        {
            return [];
        }

        var subpaths = new List<SubPath>();
        var current = new List<PathCommand>();
        foreach (var command in all)
        {
            if (command.Command is 'M' or 'm' && current.Count > 0)
            {
                subpaths.Add(new SubPath(current, current[^1].Command is 'Z' or 'z'));
                current = [command];
            }
            else
            {
                current.Add(command);
            }
        }

        if (current.Count > 0)
        {
            subpaths.Add(new SubPath(current, current[^1].Command is 'Z' or 'z'));
        }

        return subpaths;
    }

    /// <summary>Writes commands as path data, space separated.</summary>
    public static string Serialize(IEnumerable<PathCommand> commands, int precision = 2)
    {
        return string.Join(' ', commands.Select(command => command.Serialize(precision)));
    }

    private static List<PathCommand> Split(char command, List<double> arguments)
    {
        var arity = Arity[command];
        if (arity == 0)
        {
            return [new PathCommand(command, [])];
        }

        var results = new List<PathCommand>();
        for (var start = 0; start + arity <= arguments.Count; start += arity)
        {
            var op = start > 0 && command == 'M' ? 'L' : start > 0 && command == 'm' ? 'l' : command;
            results.Add(new PathCommand(op, [.. arguments.Skip(start).Take(arity)]));
        }

        return results;
    }

    [GeneratedRegex(@"(?<cmd>[MmLlHhVvCcSsQqTtAaZz])|(?<num>[-+]?(?:[0-9]*\.[0-9]+|[0-9]+)(?:[eE][-+]?[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
