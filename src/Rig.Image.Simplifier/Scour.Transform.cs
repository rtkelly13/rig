using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

/// <summary>One transform function with its numbers; scour's <c>(type, args)</c> tuple.</summary>
internal sealed record ScourTransformFunction(string Type, List<ScourDecimal> Args);

// scour's transform list handling: svg_transform.py's parser, optimizeTransform and optimizeTransforms.
internal static partial class Scour
{
    private static readonly string[] TransformAttributes = ["transform", "patternTransform", "gradientTransform"];

    private static readonly string[] TransformTokenKinds = ["float", "int", "command", "start", "end"];

    [GeneratedRegex(
        @"(?<float>[-+]?(?:(?:[0-9]*\.[0-9]+)|(?:[0-9]+\.?))(?:[Ee][-+]?[0-9]+)?)|(?<int>[-+]?[0-9]+)|(?<command>matrix|translate|scale|rotate|skew[XY])|(?<start>\()|(?<end>\))",
        RegexOptions.CultureInvariant)]
    private static partial Regex TransformLexer();

    // Like the path lexer, anything unmatched is skipped. The null text is the end-of-input token.
    private static List<(string Kind, string? Text)> LexTransform(string text)
    {
        var tokens = new List<(string Kind, string? Text)>();
        foreach (Match match in TransformLexer().Matches(text))
        {
            var kind = TransformTokenKinds.First(name => match.Groups[name].Success);
            tokens.Add((kind, match.Value));
        }

        tokens.Add(("eof", null));
        return tokens;
    }

    /// <summary>svg_transform.py's parser. A <see cref="FormatException"/> is its SyntaxError.</summary>
    private static List<ScourTransformFunction> ParseTransform(string text)
    {
        var tokens = LexTransform(text);
        var position = 0;
        var result = new List<ScourTransformFunction>();
        while (tokens[position].Kind != "eof")
        {
            if (tokens[position].Kind != "command")
            {
                throw new FormatException("expecting a transformation type");
            }

            var type = tokens[position].Text!;
            position++;
            if (tokens[position].Kind != "start")
            {
                throw new FormatException("expecting '('");
            }

            position++;
            var (mandatory, optional) = type switch
            {
                "skewX" or "skewY" => (1, 0),
                "rotate" => (1, 2),
                "matrix" => (6, 0),
                _ => (1, 1),
            };

            var numbers = new List<ScourDecimal>();
            for (var i = 0; i < mandatory; i++)
            {
                numbers.Add(ReadTransformNumber(tokens, ref position));
            }

            // rotate takes a centre (both numbers or neither); translate and scale an optional second number.
            if (type == "rotate")
            {
                if (tokens[position].Kind is "float" or "int")
                {
                    numbers.Add(ReadTransformNumber(tokens, ref position));
                    numbers.Add(ReadTransformNumber(tokens, ref position));
                }
            }
            else if (optional == 1 && tokens[position].Kind is "float" or "int")
            {
                numbers.Add(ReadTransformNumber(tokens, ref position));
            }

            if (tokens[position].Kind != "end")
            {
                throw new FormatException("expecting ')'");
            }

            position++;
            result.Add(new ScourTransformFunction(type, numbers));
        }

        return result;
    }

    private static ScourDecimal ReadTransformNumber(List<(string Kind, string? Text)> tokens, ref int position)
    {
        if (tokens[position].Kind is not ("float" or "int"))
        {
            throw new FormatException("expecting a number");
        }

        var value = Create(tokens[position].Text!);
        position++;
        return value;
    }

    // Any rotation fits within 360 degrees; the range [-90, 270[ keeps the number short.
    private static ScourDecimal OptimizeAngle(ScourDecimal angle)
    {
        angle = angle.IsNegative ? angle % -360 : angle % 360;
        if (angle >= 270)
        {
            angle -= 360;
        }
        else if (angle < -90)
        {
            angle += 360;
        }

        return angle;
    }

    private static void OptimizeTransform(List<ScourTransformFunction> transform)
    {
        if (transform.Count == 1 && transform[0].Type == "matrix")
        {
            MatrixToSimpler(transform);
        }

        SimplifyOptionalNumbers(transform);
        CoalesceRuns(transform);

        // Single-element fixups: the coalescing above needs two elements.
        var i = 0;
        while (i < transform.Count)
        {
            var (type, args) = (transform[i].Type, transform[i].Args);
            if (type is "skewX" or "skewY" or "rotate" && args.Count == 1 && args[0].IsZero)
            {
                transform.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    // A single matrix(a b c d e f) as a shorter function: identity, translate, scale or rotate.
    private static void MatrixToSimpler(List<ScourTransformFunction> transform)
    {
        var m = transform[0].Args;
        ScourDecimal a1 = m[0];
        ScourDecimal b1 = m[1];
        ScourDecimal a2 = m[2];
        ScourDecimal b2 = m[3];
        ScourDecimal a3 = m[4];
        ScourDecimal b3 = m[5];
        if (a1 == 1 && b1 == 0 && a2 == 0 && b2 == 1 && a3 == 0 && b3 == 0)
        {
            transform.RemoveAt(0);
        }
        else if (a1 == 1 && a2 == 0 && b1 == 0 && b2 == 1)
        {
            transform[0] = new ScourTransformFunction("translate", [a3, b3]);
        }
        else if (a2 == 0 && a3 == 0 && b1 == 0 && b3 == 0)
        {
            transform[0] = new ScourTransformFunction("scale", [a1, b2]);
        }
        else if (a1 == b2 && a1 >= -1 && a1 <= 1 && a3 == 0 && -b1 == a2 && b1 >= -1 && b1 <= 1 && b3 == 0
            && ((b1 * b1) + (a1 * a1) - 1).Abs() < ScourDecimal.Parse("1e-15"))
        {
            // The signs of sin and cos place the angle in a quadrant; asin has the right range for the middle two.
            var angle = ScourDecimal.FromDouble(Math.Asin(DecimalToDouble(b1)) * (180.0 / Math.PI));
            if (a1.IsNegative)
            {
                angle = b1.IsNegative ? -180 - angle : 180 - angle;
            }

            transform[0] = new ScourTransformFunction("rotate", [angle]);
        }
    }

    // Python's float(Decimal): the nearest double to the decimal's value.
    private static double DecimalToDouble(ScourDecimal value) => double.Parse(value.ToPlainString(), System.Globalization.CultureInfo.InvariantCulture);

    private static void SimplifyOptionalNumbers(List<ScourTransformFunction> transform)
    {
        foreach (var (type, args) in transform.Select(t => (t.Type, t.Args)))
        {
            switch (type)
            {
                case "translate":
                    if (args.Count == 2 && args[1].IsZero)
                    {
                        args.RemoveAt(1);
                    }

                    break;
                case "rotate":
                    args[0] = OptimizeAngle(args[0]);
                    if (args.Count == 3 && args[1].IsZero && args[2].IsZero)
                    {
                        args.RemoveRange(1, 2);
                    }

                    break;
                case "scale":
                    if (args.Count == 2 && args[0] == args[1])
                    {
                        args.RemoveAt(1);
                    }

                    break;
            }
        }
    }

    // The merged entry at i - 1 is an identity: drop it. What followed it now sits at i - 1, and the next comparison needs a predecessor, so the index never goes below 1.
    private static void RemoveIdentityBefore(List<ScourTransformFunction> transform, ref int i)
    {
        transform.RemoveAt(i - 1);
        i = Math.Max(i - 1, 1);
    }

    // Runs of the same translate, rotate (about the origin) or scale are safe to combine.
    private static void CoalesceRuns(List<ScourTransformFunction> transform)
    {
        var i = 1;
        while (i < transform.Count)
        {
            var (currType, currArgs) = (transform[i].Type, transform[i].Args);
            var (prevType, prevArgs) = (transform[i - 1].Type, transform[i - 1].Args);
            if (currType == prevType && currType == "translate")
            {
                prevArgs[0] += currArgs[0];
                if (currArgs.Count == 2)
                {
                    if (prevArgs.Count == 2)
                    {
                        prevArgs[1] += currArgs[1];
                    }
                    else if (prevArgs.Count == 1)
                    {
                        prevArgs.Add(currArgs[1]);
                    }
                }

                transform.RemoveAt(i);

                // Python indexes prevArgs[1] here, which raises for a one-number translation: it is not an identity unless that is zero too.
                if (prevArgs[0].IsZero && (prevArgs.Count < 2 || prevArgs[1].IsZero))
                {
                    RemoveIdentityBefore(transform, ref i);
                }
            }
            else if (currType == prevType && currType == "rotate" && prevArgs.Count == 1 && currArgs.Count == 1)
            {
                prevArgs[0] = OptimizeAngle(prevArgs[0] + currArgs[0]);
                transform.RemoveAt(i);
            }
            else if (currType == prevType && currType == "scale")
            {
                var previousX = prevArgs[0];
                prevArgs[0] *= currArgs[0];
                if (prevArgs.Count == 2 && currArgs.Count == 2)
                {
                    prevArgs[1] *= currArgs[1];
                }
                else if (prevArgs.Count == 1 && currArgs.Count == 2)
                {
                    prevArgs.Add(previousX * currArgs[1]);
                }
                else if (prevArgs.Count == 2 && currArgs.Count == 1)
                {
                    prevArgs[1] *= currArgs[0];
                }

                transform.RemoveAt(i);
                if (prevArgs[0] == 1 && (prevArgs.Count == 1 || prevArgs[1] == 1))
                {
                    RemoveIdentityBefore(transform, ref i);
                }
            }
            else
            {
                i++;
            }
        }
    }

    private static string SerializeTransform(List<ScourTransformFunction> transform) =>
        string.Join(' ', transform.Select(t => t.Type + "(" + string.Join(' ', t.Args.Select(n => ScourNumbers.Unitless(n))) + ")"));

    private static void OptimizeTransforms(DomElement element)
    {
        foreach (var attribute in TransformAttributes)
        {
            var value = element.GetAttribute(attribute);
            if (value.Length == 0)
            {
                continue;
            }

            var transform = ParseTransform(value);
            OptimizeTransform(transform);
            var shorter = SerializeTransform(transform);
            if (shorter.Length < value.Length)
            {
                if (shorter.Length > 0)
                {
                    element.SetAttribute(attribute, shorter);
                }
                else
                {
                    element.RemoveAttribute(attribute);
                }
            }
        }

        foreach (var child in element.ChildElements.ToList())
        {
            OptimizeTransforms(child);
        }
    }
}
