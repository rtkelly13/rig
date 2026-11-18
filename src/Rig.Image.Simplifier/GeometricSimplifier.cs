using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace Rig.Image.Simplifier;

/// <summary>
/// Geometric decimation: topology-preserving Douglas-Peucker on straight-sided subpaths, and flat-curve collapse on the rest. This is
/// the original <c>simplify_path_geometry</c>, with NetTopologySuite standing in for shapely. Both are ports of the same JTS
/// algorithm, so where GEOS and NTS disagree it is on which vertex a ring starts at or on the order of a result's rings, not on what is drawn.
/// </summary>
public static class GeometricSimplifier
{
    private static readonly GeometryFactory Factory = new();

    /// <summary>Simplifies path data and returns it written at two places. Data with no commands comes back as it was.</summary>
    public static string SimplifyPath(string d, double tolerance = 0.5)
    {
        var subpaths = PathData.ExtractSubpaths(d);
        if (subpaths.Count == 0)
        {
            return d;
        }

        var output = new List<PathCommand>();
        var position = new Point2(0, 0);
        foreach (var subpath in subpaths)
        {
            output.AddRange(SimplifySubpath(subpath, tolerance, position));
            position = EndPoint(subpath.Commands, position);
        }

        return PathData.Serialize(output);
    }

    /// <summary>The perpendicular distance from a point to a segment.</summary>
    public static double PointToSegmentDistance(double px, double py, double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0)
        {
            return double.Hypot(px - x1, py - y1);
        }

        var t = Math.Max(0.0, Math.Min(1.0, (((px - x1) * dx) + ((py - y1) * dy)) / lengthSquared));
        return double.Hypot(px - (x1 + (t * dx)), py - (y1 + (t * dy)));
    }

    private static Point2 Advance(PathCommand command, Point2 position, Point2 start)
    {
        var c = command.Command;
        var args = command.Args;
        var relative = char.IsLower(c);
        if (c is 'Z' or 'z')
        {
            return start;
        }

        if (c is 'H' or 'h' && args.Count > 0)
        {
            return new Point2(args[0] + (relative ? position.X : 0.0), position.Y);
        }

        if (c is 'V' or 'v' && args.Count > 0)
        {
            return new Point2(position.X, args[0] + (relative ? position.Y : 0.0));
        }

        if (args.Count >= 2)
        {
            var x = args[^2];
            var y = args[^1];
            return relative ? new Point2(position.X + x, position.Y + y) : new Point2(x, y);
        }

        return position;
    }

    private static Point2 EndPoint(IReadOnlyList<PathCommand> commands, Point2 origin)
    {
        var position = origin;
        var start = origin;
        foreach (var command in commands)
        {
            position = Advance(command, position, start);
            if (command.Command is 'M' or 'm')
            {
                start = position;
            }
        }

        return position;
    }

    private static IReadOnlyList<PathCommand> SimplifySubpath(SubPath subpath, double tolerance, Point2 origin)
    {
        if (subpath.Commands.Count == 0)
        {
            return [];
        }

        var isLinear = subpath.Commands.All(command => char.ToUpperInvariant(command.Command) is 'M' or 'L' or 'H' or 'V' or 'Z');
        return isLinear ? SimplifyLinear(subpath, tolerance, origin) : SimplifyCurved(subpath, tolerance, origin);
    }

    private static IReadOnlyList<PathCommand> SimplifyLinear(SubPath subpath, double tolerance, Point2 origin)
    {
        var coordinates = Coordinates(subpath.Commands, origin);
        if (coordinates.Count < 3)
        {
            return subpath.Commands;
        }

        return subpath.IsClosed ? SimplifyClosed(subpath, coordinates, tolerance) : SimplifyOpen(subpath, coordinates, tolerance);
    }

    private static List<Coordinate> Coordinates(IReadOnlyList<PathCommand> commands, Point2 origin)
    {
        var coordinates = new List<Coordinate>();
        var x = origin.X;
        var y = origin.Y;
        foreach (var command in commands)
        {
            var args = command.Args;
            switch (command.Command)
            {
                case 'M' or 'L' when args.Count >= 2:
                    (x, y) = (args[0], args[1]);
                    break;
                case 'H' when args.Count >= 1:
                    x = args[0];
                    break;
                case 'h' when args.Count >= 1:
                    x += args[0];
                    break;
                case 'V' when args.Count >= 1:
                    y = args[0];
                    break;
                case 'v' when args.Count >= 1:
                    y += args[0];
                    break;
                case 'm' or 'l' when args.Count >= 2:
                    x += args[0];
                    y += args[1];
                    break;
                default:
                    continue;
            }

            coordinates.Add(new Coordinate(x, y));
        }

        return coordinates;
    }

    private static IReadOnlyList<PathCommand> SimplifyClosed(SubPath subpath, List<Coordinate> coordinates, double tolerance)
    {
        Geometry simplified;
        try
        {
            // shapely closes the ring itself and raises when fewer than four coordinates result.
            var ring = new List<Coordinate>(coordinates);
            if (!ring[0].Equals2D(ring[^1]))
            {
                ring.Add(ring[0].Copy());
            }

            Geometry polygon = Factory.CreatePolygon(Factory.CreateLinearRing([.. ring]));
            if (!polygon.IsValid)
            {
                polygon = polygon.Buffer(0);
            }

            simplified = TopologyPreservingSimplifier.Simplify(polygon, tolerance);
        }
        catch (ArgumentException)
        {
            return subpath.Commands;
        }

        if (simplified.IsEmpty)
        {
            return subpath.Commands;
        }

        var commands = new List<PathCommand>();
        switch (simplified)
        {
            case Polygon polygon:
                commands.AddRange(PolygonCommands(polygon));
                break;
            case MultiPolygon multi:
                foreach (var part in multi.Geometries.Cast<Polygon>())
                {
                    commands.AddRange(PolygonCommands(part));
                }

                break;
            default:
                break;
        }

        return commands;
    }

    private static IEnumerable<PathCommand> PolygonCommands(Polygon polygon)
    {
        foreach (var command in RingCommands(polygon.ExteriorRing.Coordinates))
        {
            yield return command;
        }

        foreach (var interior in polygon.InteriorRings)
        {
            foreach (var command in RingCommands(interior.Coordinates))
            {
                yield return command;
            }
        }
    }

    private static List<PathCommand> RingCommands(Coordinate[] ring)
    {
        var points = ring.ToList();
        if (points.Count > 1 && points[0].Equals2D(points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }

        var commands = new List<PathCommand> { new('M', [points[0].X, points[0].Y]) };
        commands.AddRange(points.Skip(1).Select(point => new PathCommand('L', [point.X, point.Y])));
        commands.Add(new PathCommand('Z', []));
        return commands;
    }

    private static IReadOnlyList<PathCommand> SimplifyOpen(SubPath subpath, List<Coordinate> coordinates, double tolerance)
    {
        var simplified = TopologyPreservingSimplifier.Simplify(Factory.CreateLineString([.. coordinates]), tolerance);
        var points = simplified.Coordinates;
        if (simplified.IsEmpty || points.Length == 0)
        {
            return subpath.Commands;
        }

        var commands = new List<PathCommand> { new('M', [points[0].X, points[0].Y]) };
        commands.AddRange(points.Skip(1).Select(point => new PathCommand('L', [point.X, point.Y])));
        return commands;
    }

    private static List<PathCommand> SimplifyCurved(SubPath subpath, double tolerance, Point2 origin)
    {
        var output = new List<PathCommand>();
        var current = origin;
        var start = origin;
        foreach (var command in subpath.Commands)
        {
            var c = command.Command;
            var relative = char.IsLower(c);
            if (c is 'C' or 'c' && command.Args.Count >= 6)
            {
                var baseX = relative ? current.X : 0.0;
                var baseY = relative ? current.Y : 0.0;
                var p1 = new Point2(baseX + command.Args[0], baseY + command.Args[1]);
                var p2 = new Point2(baseX + command.Args[2], baseY + command.Args[3]);
                var p3 = new Point2(baseX + command.Args[4], baseY + command.Args[5]);
                var nearLinear = PointToSegmentDistance(p1.X, p1.Y, current.X, current.Y, p3.X, p3.Y) <= tolerance
                    && PointToSegmentDistance(p2.X, p2.Y, current.X, current.Y, p3.X, p3.Y) <= tolerance;
                output.Add(nearLinear ? new PathCommand(relative ? 'l' : 'L', [command.Args[4], command.Args[5]]) : command);
                current = p3;
            }
            else if (c is 'L' or 'l' && command.Args.Count >= 2)
            {
                var target = Advance(command, current, start);
                if (double.Hypot(target.X - current.X, target.Y - current.Y) > tolerance * 0.1)
                {
                    output.Add(command);
                    current = target;
                }
            }
            else
            {
                output.Add(command);
                current = Advance(command, current, start);
                if (c is 'M' or 'm')
                {
                    start = current;
                }
            }
        }

        return output;
    }

    private readonly record struct Point2(double X, double Y);
}
