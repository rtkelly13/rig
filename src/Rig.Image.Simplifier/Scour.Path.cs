using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

/// <summary>One path command with its numbers; scour's <c>(cmd, data)</c> tuple.</summary>
internal readonly record struct ScourSegment(char Command, List<ScourDecimal> Data);

// scour's path and point data cleanup: the parser from svg_regex.py, cleanPath, cleanPolygon and cleanPolyline, and the serialiser.
internal static partial class Scour
{
    private static readonly Regex CommaOrSpaceSeparator = CommaOrSpace();

    [GeneratedRegex(
        @"(?<float>[-+]?(?:(?:[0-9]*\.[0-9]+)|(?:[0-9]+\.?))(?:[Ee][-+]?[0-9]+)?)|(?<int>[-+]?[0-9]+)|(?<command>[AaCcHhLlMmQqSsTtVvZz])",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathLexer();

    // Python's regex finditer skips anything the lexicon does not match, and ends with an EOF token (a null text).
    private static List<PathToken> LexPath(string text)
    {
        var tokens = new List<PathToken>();
        foreach (Match match in PathLexer().Matches(text))
        {
            var kind = match.Groups["command"].Success ? PathTokenKind.Command : match.Groups["float"].Success ? PathTokenKind.Float : PathTokenKind.Int;
            tokens.Add(new PathToken(kind, match.Value));
        }

        tokens.Add(new PathToken(PathTokenKind.End, null));
        return tokens;
    }

    private static ScourDecimal Create(string text) => ScourDecimal.Parse(text).Plus(ScourDecimal.ContextPrecision);

    /// <summary>svg_regex's parser. A <see cref="FormatException"/> is Python's SyntaxError, which the caller handles by leaving the document alone.</summary>
    private static List<ScourSegment> ParsePath(string text)
    {
        var reader = new PathTokenReader(LexPath(text));
        var segments = new List<ScourSegment>();
        while (reader.Current.Kind != PathTokenKind.End)
        {
            if (reader.Current.Kind != PathTokenKind.Command)
            {
                throw new FormatException($"expecting a command; got {reader.Current}");
            }

            var command = reader.Current.Text![0];
            reader.Advance();
            var data = new List<ScourDecimal>();
            switch (char.ToUpperInvariant(command))
            {
                case 'Z':
                    break;
                case 'H' or 'V':
                    while (reader.Current.IsNumber)
                    {
                        data.Add(reader.Coordinate());
                    }

                    break;
                case 'A':
                    ParseArcs(reader, data);
                    break;
                default:
                    var pairs = char.ToUpperInvariant(command) switch
                    {
                        'C' => 3,
                        'S' or 'Q' => 2,
                        _ => 1,
                    };
                    while (reader.Current.IsNumber)
                    {
                        for (var i = 0; i < pairs * 2; i++)
                        {
                            data.Add(reader.Coordinate());
                        }
                    }

                    break;
            }

            segments.Add(new ScourSegment(command, data));
        }

        return segments;
    }

    private static void ParseArcs(PathTokenReader reader, List<ScourDecimal> arguments)
    {
        while (reader.Current.IsNumber)
        {
            var radiusX = reader.Coordinate();
            if (radiusX.IsNegative)
            {
                throw new FormatException("expecting a nonnegative number");
            }

            var radiusY = reader.Coordinate();
            if (radiusY.IsNegative)
            {
                throw new FormatException("expecting a nonnegative number");
            }

            var rotation = reader.Coordinate();
            var largeArc = reader.Flag();
            var sweep = reader.Flag();
            var x = reader.Coordinate();
            var y = reader.Coordinate();
            arguments.AddRange([radiusX, radiusY, rotation, largeArc, sweep, x, y]);
        }
    }

    /// <summary>scour's <c>cleanPath</c>: relative commands, empty and redundant segments dropped, shorthands, runs merged.</summary>
    private static void CleanPath(DomElement element)
    {
        var oldPath = element.GetAttribute("d");
        var path = ParsePath(oldPath);
        if (path.Count == 0)
        {
            return;
        }

        var style = ReadStyle(element);
        var roundOrSquareCaps = element.GetAttribute("stroke-linecap") is "round" or "square"
            || (style.TryGetValue("stroke-linecap", out var cap) && cap is "round" or "square");
        var intermediateMarkers = element.HasAttribute("marker") || element.HasAttribute("marker-mid") || style.ContainsKey("marker") || style.ContainsKey("marker-mid");

        MakeRelative(path);
        if (!roundOrSquareCaps)
        {
            RemoveEmptySegments(path);
            RemoveNoOps(path);
        }

        // Delete subcommands having no coordinates.
        path = [.. path.Where(s => s.Data.Count > 0 || s.Command == 'z')];
        if (path.Count == 0)
        {
            return;
        }

        path = StraightCurvesToLines(path);
        path = Collapse(path, mergeMoveLine: true);
        path = ToShorthands(path);
        if (!intermediateMarkers)
        {
            CollapseSameDirection(path);
        }

        path = Collapse(path, mergeMoveLine: false);
        var newPath = SerializePath(path);

        // If for whatever reason the path got longer, keep the old one.
        if (newPath.Length <= oldPath.Length)
        {
            element.SetAttribute("d", newPath);
        }
    }

    private static void MakeRelative(List<ScourSegment> path)
    {
        ScourDecimal x = 0;
        ScourDecimal y = 0;
        ScourDecimal startX = 0;
        ScourDecimal startY = 0;
        for (var index = 0; index < path.Count; index++)
        {
            var (command, data) = path[index];
            switch (command)
            {
                case 'A':
                    for (var i = 0; i < data.Count; i += 7)
                    {
                        data[i + 5] -= x;
                        data[i + 6] -= y;
                        x += data[i + 5];
                        y += data[i + 6];
                    }

                    path[index] = new ScourSegment('a', data);
                    break;
                case 'a':
                    x += Sum(data, 5, 7);
                    y += Sum(data, 6, 7);
                    break;
                case 'H':
                    for (var i = 0; i < data.Count; i++)
                    {
                        data[i] -= x;
                        x += data[i];
                    }

                    path[index] = new ScourSegment('h', data);
                    break;
                case 'h':
                    x += Sum(data, 0, 1);
                    break;
                case 'V':
                    for (var i = 0; i < data.Count; i++)
                    {
                        data[i] -= y;
                        y += data[i];
                    }

                    path[index] = new ScourSegment('v', data);
                    break;
                case 'v':
                    y += Sum(data, 0, 1);
                    break;
                case 'M':
                    startX = data[0];
                    startY = data[1];

                    // The first moveto stays absolute: relative to the origin it would only become (0, 0).
                    if (index != 0)
                    {
                        data[0] -= x;
                        data[1] -= y;
                    }

                    x = startX;
                    y = startY;
                    for (var i = 2; i < data.Count; i += 2)
                    {
                        data[i] -= x;
                        data[i + 1] -= y;
                        x += data[i];
                        y += data[i + 1];
                    }

                    path[index] = new ScourSegment('m', data);
                    break;
                case 'L' or 'T':
                    for (var i = 0; i < data.Count; i += 2)
                    {
                        data[i] -= x;
                        data[i + 1] -= y;
                        x += data[i];
                        y += data[i + 1];
                    }

                    path[index] = new ScourSegment(char.ToLowerInvariant(command), data);
                    break;
                case 'm':
                    var from = 0;
                    if (index == 0)
                    {
                        // Start of the path: an absolute moveto followed by relative linetos.
                        startX = data[0];
                        startY = data[1];
                        x = startX;
                        y = startY;
                        from = 2;
                    }
                    else
                    {
                        startX = x + data[0];
                        startY = y + data[1];
                    }

                    for (var i = from; i < data.Count; i += 2)
                    {
                        x += data[i];
                        y += data[i + 1];
                    }

                    break;
                case 'l' or 't':
                    x += Sum(data, 0, 2);
                    y += Sum(data, 1, 2);
                    break;
                case 'S' or 'Q':
                    for (var i = 0; i < data.Count; i += 4)
                    {
                        data[i] -= x;
                        data[i + 1] -= y;
                        data[i + 2] -= x;
                        data[i + 3] -= y;
                        x += data[i + 2];
                        y += data[i + 3];
                    }

                    path[index] = new ScourSegment(char.ToLowerInvariant(command), data);
                    break;
                case 's' or 'q':
                    x += Sum(data, 2, 4);
                    y += Sum(data, 3, 4);
                    break;
                case 'C':
                    for (var i = 0; i < data.Count; i += 6)
                    {
                        for (var offset = 0; offset < 6; offset += 2)
                        {
                            data[i + offset] -= x;
                            data[i + offset + 1] -= y;
                        }

                        x += data[i + 4];
                        y += data[i + 5];
                    }

                    path[index] = new ScourSegment('c', data);
                    break;
                case 'c':
                    x += Sum(data, 4, 6);
                    y += Sum(data, 5, 6);
                    break;
                case 'z' or 'Z':
                    x = startX;
                    y = startY;
                    path[index] = new ScourSegment('z', data);
                    break;
            }
        }
    }

    // Python's sum(data[start::step]): the context rounds each partial sum.
    private static ScourDecimal Sum(List<ScourDecimal> data, int start, int step)
    {
        ScourDecimal total = 0;
        for (var i = start; i < data.Count; i += step)
        {
            total += data[i];
        }

        return total;
    }

    private static void RemoveEmptySegments(List<ScourSegment> path)
    {
        for (var index = 0; index < path.Count; index++)
        {
            var (command, data) = path[index];
            switch (command)
            {
                case 'm' or 'l' or 't':
                    // "m0 0 ... z" differs from "l...z", so a moveto's own pair is never judged here.
                    var i = command == 'm' ? 2 : 0;
                    while (i < data.Count)
                    {
                        if (data[i].IsZero && data[i + 1].IsZero)
                        {
                            data.RemoveRange(i, 2);
                        }
                        else
                        {
                            i += 2;
                        }
                    }

                    break;
                case 'c':
                    RemoveZeroRuns(data, 6);
                    break;
                case 'a':
                    RemoveZeroRuns(data, 7, 5);
                    break;
                case 'q':
                    RemoveZeroRuns(data, 4);
                    break;
                case 'h' or 'v':
                    path[index] = new ScourSegment(command, [.. data.Where(c => !c.IsZero)]);
                    break;
            }
        }
    }

    // Drops each group of `size` numbers whose last numbers (from `firstZero`) are all zero; for most commands that is the whole group.
    private static void RemoveZeroRuns(List<ScourDecimal> data, int size, int firstZero = 0)
    {
        var i = 0;
        while (i < data.Count)
        {
            if (data.Skip(i + firstZero).Take(size - firstZero).All(n => n.IsZero))
            {
                data.RemoveRange(i, size);
            }
            else
            {
                i += size;
            }
        }
    }

    private static void RemoveNoOps(List<ScourSegment> path)
    {
        var index = path.Count;
        var subpathNeedsAnchor = false;

        // The first moveto can never be rewritten (except when it is the only command).
        while (index > 1)
        {
            index--;
            var (command, data) = path[index];
            if (command == 'z')
            {
                var (nextCommand, nextData) = path[index - 1];
                if (nextCommand == 'm' && nextData.Count == 2)
                {
                    // "mX Yz" is "mX Y"; "m0 0 1 1z" is not safe to rewrite, as it matters where the pen ends.
                    path.RemoveAt(index);
                }
                else
                {
                    subpathNeedsAnchor = true;
                }
            }
            else if (command == 'm')
            {
                if (path.Count - 1 == index && data.Count == 2)
                {
                    // Ends with an empty move.
                    path.RemoveAt(index);
                    continue;
                }

                if (subpathNeedsAnchor)
                {
                    subpathNeedsAnchor = false;
                }
                else if (data[0].IsZero && data[1].IsZero)
                {
                    // Unanchored: no z follows, so "m0 0 ..." is just "l...".
                    path[index] = new ScourSegment('l', data[2..]);
                }
            }
        }
    }

    private static List<ScourSegment> StraightCurvesToLines(List<ScourSegment> path)
    {
        var result = new List<ScourSegment> { path[0] };
        foreach (var (command, data) in path.Skip(1))
        {
            var newData = data;
            if (command == 'c')
            {
                newData = [];
                for (var i = 0; i < data.Count; i += 6)
                {
                    // Relative, so the previous point is (0, 0) and the new one is (dx, dy).
                    var dx = data[i + 4];
                    var dy = data[i + 5];
                    var straight = dx.IsZero
                        ? data[i].IsZero && data[i + 2].IsZero
                        : data[i + 1] == (dy / dx) * data[i] && data[i + 3] == (dy / dx) * data[i + 2];
                    if (straight)
                    {
                        if (newData.Count > 0)
                        {
                            result.Add(new ScourSegment(command, newData));
                            newData = [];
                        }

                        result.Add(new ScourSegment('l', [dx, dy]));
                    }
                    else
                    {
                        newData.AddRange(data.GetRange(i, 6));
                    }
                }
            }

            if (newData.Count > 0 || command is 'z' or 'Z')
            {
                result.Add(new ScourSegment(command, newData));
            }
        }

        return result;
    }

    // Merges consecutive commands of one type. With mergeMoveLine a lineto also joins a moveto (its extra pairs are implicit linetos);
    // a moveto never joins anything, as each draws nothing of its own.
    private static List<ScourSegment> Collapse(List<ScourSegment> path, bool mergeMoveLine)
    {
        if (mergeMoveLine)
        {
            var merged = new List<ScourSegment>();
            var previous = default(ScourSegment);
            var hasPrevious = false;
            foreach (var segment in path)
            {
                if (!hasPrevious)
                {
                    previous = segment;
                    hasPrevious = true;
                }
                else if (segment.Command != 'm' && (segment.Command == previous.Command || (segment.Command == 'l' && previous.Command == 'm')))
                {
                    previous.Data.AddRange(segment.Data);
                }
                else
                {
                    merged.Add(previous);
                    previous = segment;
                }
            }

            merged.Add(previous);
            return merged;
        }

        var result = new List<ScourSegment> { path[0] };
        var pending = default(ScourSegment);
        var hasPending = false;
        foreach (var segment in path.Skip(1))
        {
            if (hasPending && (segment.Command != pending.Command || segment.Command == 'm'))
            {
                result.Add(pending);
                hasPending = false;
            }

            if (hasPending && segment.Command == pending.Command && segment.Command != 'm')
            {
                pending.Data.AddRange(segment.Data);
            }
            else
            {
                pending = segment;
                hasPending = true;
            }
        }

        if (hasPending)
        {
            result.Add(pending);
        }

        return result;
    }

    private static List<ScourSegment> ToShorthands(List<ScourSegment> path)
    {
        var result = new List<ScourSegment>();
        foreach (var (originalCommand, data) in path)
        {
            var command = originalCommand;
            switch (command)
            {
                case 'l':
                    {
                        var run = new List<ScourDecimal>();
                        for (var i = 0; i < data.Count; i += 2)
                        {
                            if (data[i].IsZero)
                            {
                                FlushRun(result, ref command, run, advance: false);
                                result.Add(new ScourSegment('v', [data[i + 1]]));
                            }
                            else if (data[i + 1].IsZero)
                            {
                                FlushRun(result, ref command, run, advance: false);
                                result.Add(new ScourSegment('h', [data[i]]));
                            }
                            else
                            {
                                run.AddRange(data.GetRange(i, 2));
                            }
                        }

                        if (run.Count > 0)
                        {
                            result.Add(new ScourSegment('l', run));
                        }

                        break;
                    }

                case 'm':
                    {
                        // The implied relative linetos after the moveto.
                        var run = new List<ScourDecimal> { data[0], data[1] };
                        for (var i = 2; i < data.Count; i += 2)
                        {
                            if (data[i].IsZero)
                            {
                                FlushRun(result, ref command, run, advance: true);
                                result.Add(new ScourSegment('v', [data[i + 1]]));
                            }
                            else if (data[i + 1].IsZero)
                            {
                                FlushRun(result, ref command, run, advance: true);
                                result.Add(new ScourSegment('h', [data[i]]));
                            }
                            else
                            {
                                run.AddRange(data.GetRange(i, 2));
                            }
                        }

                        if (run.Count > 0)
                        {
                            result.Add(new ScourSegment(command, run));
                        }

                        break;
                    }

                case 'c':
                    ToSmoothCurves(result, data);
                    break;
                case 'q':
                    ToSmoothQuadratics(result, data);
                    break;
                default:
                    result.Add(new ScourSegment(command, data));
                    break;
            }
        }

        return result;
    }

    // Writes the pending run as its own command, then (for a moveto) carries on in linetos.
    private static void FlushRun(List<ScourSegment> result, ref char command, List<ScourDecimal> run, bool advance)
    {
        if (run.Count == 0)
        {
            return;
        }

        result.Add(new ScourSegment(advance ? command : 'l', [.. run]));
        run.Clear();
        if (advance)
        {
            command = 'l';
        }
    }

    private static void ToSmoothCurves(List<ScourSegment> result, List<ScourDecimal> data)
    {
        // The control point a smooth curve would assume: the current point, or the previous curve's second control point reflected.
        ScourDecimal controlX = 0;
        ScourDecimal controlY = 0;
        if (result.Count > 0 && result[^1] is { Command: 's', Data: var previous })
        {
            controlX = previous[^2] - previous[^4];
            controlY = previous[^1] - previous[^3];
        }

        var run = new List<ScourDecimal>();
        for (var i = 0; i < data.Count; i += 6)
        {
            if (controlX == data[i] && controlY == data[i + 1])
            {
                if (run.Count > 0)
                {
                    result.Add(new ScourSegment('c', [.. run]));
                    run.Clear();
                }

                result.Add(new ScourSegment('s', data.GetRange(i + 2, 4)));
            }
            else
            {
                run.AddRange(data.GetRange(i, 6));
            }

            controlX = data[i + 4] - data[i + 2];
            controlY = data[i + 5] - data[i + 3];
        }

        if (run.Count > 0)
        {
            result.Add(new ScourSegment('c', run));
        }
    }

    private static void ToSmoothQuadratics(List<ScourSegment> result, List<ScourDecimal> data)
    {
        ScourDecimal controlX = 0;
        ScourDecimal controlY = 0;
        var run = new List<ScourDecimal>();
        for (var i = 0; i < data.Count; i += 4)
        {
            if (controlX == data[i] && controlY == data[i + 1])
            {
                if (run.Count > 0)
                {
                    result.Add(new ScourSegment('q', [.. run]));
                    run.Clear();
                }

                result.Add(new ScourSegment('t', data.GetRange(i + 2, 2)));
            }
            else
            {
                run.AddRange(data.GetRange(i, 4));
            }

            controlX = data[i + 2] - data[i];
            controlY = data[i + 3] - data[i + 1];
        }

        if (run.Count > 0)
        {
            result.Add(new ScourSegment('q', run));
        }
    }

    // Collapses coordinates that run in the same direction: "h-100-100" is "h-200", "h300-100" stays.
    private static void CollapseSameDirection(List<ScourSegment> path)
    {
        foreach (var (command, data) in path)
        {
            if (command is 'h' or 'v' && data.Count >= 2)
            {
                var i = 0;
                while (i + 1 < data.Count)
                {
                    if (IsSameSign(data[i], data[i + 1]))
                    {
                        data[i] += data[i + 1];
                        data.RemoveAt(i + 1);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            else if ((command == 'l' && data.Count >= 4) || (command == 'm' && data.Count >= 6))
            {
                // A moveto's first pair is not drawn, so the runs start after it.
                var i = command == 'm' ? 2 : 0;
                while (i + 2 < data.Count)
                {
                    if (IsSameDirection(data[i], data[i + 1], data[i + 2], data[i + 3]))
                    {
                        data[i] += data[i + 2];
                        data[i + 1] += data[i + 3];
                        data.RemoveRange(i + 2, 2);
                    }
                    else
                    {
                        i += 2;
                    }
                }
            }
        }
    }

    private static bool IsSameSign(ScourDecimal a, ScourDecimal b) => (a <= 0 && b <= 0) || (a >= 0 && b >= 0);

    private static bool IsSameDirection(ScourDecimal x1, ScourDecimal y1, ScourDecimal x2, ScourDecimal y2)
    {
        if (!IsSameSign(x1, x2) || !IsSameSign(y1, y2))
        {
            return false;
        }

        var difference = (y1 / x1) - (y2 / x2);
        return (1 + difference).Plus(ScourNumbers.Digits) == 1;
    }

    private static string SerializePath(List<ScourSegment> path) => string.Concat(path.Select(s => s.Command + Coordinates(s.Data, false)));

    /// <summary>
    /// scour's <c>scourCoordinates</c>: numbers at five significant digits, a space only where one is needed. The renderer workaround
    /// (on by default) keeps a leading zero and always separates the numbers after an arc's flags.
    /// </summary>
    private static string Coordinates(List<ScourDecimal> data, bool forceWhitespace)
    {
        var parts = new List<string>();
        var previous = string.Empty;
        for (var c = 0; c < data.Count; c++)
        {
            var scoured = ScourNumbers.Unitless(data[c], rendererWorkaround: true);
            if (c > 0 && (forceWhitespace || char.IsAsciiDigit(scoured[0]) || (scoured[0] == '.' && !previous.Contains('.', StringComparison.Ordinal) && !previous.Contains('e', StringComparison.Ordinal))))
            {
                parts.Add(" ");
            }

            parts.Add(scoured);
            previous = scoured;
        }

        // A number in scientific notation needs a space before a following negative one, or the minus reads as the exponent's sign.
        for (var i = 1; i < parts.Count; i++)
        {
            if (parts[i][0] == '-' && parts[i - 1].Contains('e', StringComparison.Ordinal))
            {
                parts[i - 1] += " ";
            }
        }

        return string.Concat(parts);
    }

    private static List<ScourDecimal> ParseListOfPoints(string text)
    {
        var numbers = new List<string>();
        foreach (var token in CommaOrSpaceSeparator.Split(text.Trim()))
        {
            var negatives = token.Split('-');
            if (negatives.Length == 1)
            {
                numbers.Add(negatives[0]);
                continue;
            }

            // "100-100" is two numbers, unless the minus belongs to an exponent ("500.00e-1").
            for (var j = 0; j < negatives.Length; j++)
            {
                if (j == 0)
                {
                    if (negatives[0].Length > 0)
                    {
                        numbers.Add(negatives[0]);
                    }

                    continue;
                }

                var previous = numbers.Count > 0 ? numbers[^1] : string.Empty;
                if (previous.Length > 0 && previous[^1] is 'e' or 'E')
                {
                    numbers[^1] = previous + "-" + negatives[j];
                }
                else
                {
                    numbers.Add("-" + negatives[j]);
                }
            }
        }

        if (numbers.Count % 2 != 0)
        {
            return [];
        }

        var points = new List<ScourDecimal>();
        foreach (var number in numbers)
        {
            try
            {
                points.Add(Create(number));
            }
            catch (FormatException)
            {
                // A unit or anything else that is not a number.
                return [];
            }
        }

        return points;
    }

    private static void CleanPolygon(DomElement element)
    {
        var points = ParseListOfPoints(element.GetAttribute("points"));
        if (points.Count / 2 >= 2 && points[0] == points[^2] && points[1] == points[^1])
        {
            points.RemoveRange(points.Count - 2, 2);
        }

        element.SetAttribute("points", Coordinates(points, true));
    }

    private static void CleanPolyline(DomElement element) =>
        element.SetAttribute("points", Coordinates(ParseListOfPoints(element.GetAttribute("points")), true));

    private enum PathTokenKind
    {
        Float,
        Int,
        Command,
        End,
    }

    private readonly record struct PathToken(PathTokenKind Kind, string? Text)
    {
        public bool IsNumber => Kind is PathTokenKind.Float or PathTokenKind.Int;
    }

    // The token stream of svg_regex.py's parser, with its one-token lookahead.
    private sealed class PathTokenReader(List<PathToken> tokens)
    {
        private int _position;
        private PathToken? _replaced;

        public PathToken Current => _replaced ?? tokens[_position];

        public void Advance()
        {
            _replaced = null;
            _position = Math.Min(_position + 1, tokens.Count - 1);
        }

        /// <summary>Reads the current number and moves on.</summary>
        public ScourDecimal Coordinate()
        {
            if (!Current.IsNumber)
            {
                throw new FormatException($"expecting a number; got {Current}");
            }

            var value = Create(Current.Text!);
            Advance();
            return value;
        }

        /// <summary>An arc flag: one digit, 0 or 1, which may be written without a separator before the next number.</summary>
        public ScourDecimal Flag()
        {
            var token = Current;
            if (token.Text is null || token.Text.Length == 0 || token.Text[0] is not ('0' or '1'))
            {
                throw new FormatException($"expecting a boolean flag; got {token}");
            }

            var flag = Create(token.Text[..1]);
            if (token.Text.Length > 1)
            {
                _replaced = token with { Text = token.Text[1..] };
            }
            else
            {
                Advance();
            }

            return flag;
        }
    }
}
