using Rig.Image.Simplifier;

namespace Rig.Image.Pathways;

/// <summary>A position, or a displacement, on the 500x500 canvas (image_drift's <c>hex_geometry.Point</c>).</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct Point(double X, double Y)
{
    /// <summary>This point moved <paramref name="t"/> units along <paramref name="direction"/>.</summary>
    public Point Offset(Point direction, double t) => new(X + (direction.X * t), Y + (direction.Y * t));
}

/// <summary>The pointy-top hexagon the parametric builder is made of.</summary>
public static class HexGeometry
{
    /// <summary>The canvas centre.</summary>
    public static readonly Point Center = new(250.0, 250.0);

    private const double DegreesToRadians = Math.PI / 180.0;

    /// <summary>Python's <c>math.radians</c>.</summary>
    public static double Radians(double degrees) => degrees * DegreesToRadians;

    /// <summary>Python's <c>math.degrees</c>.</summary>
    public static double Degrees(double radians) => radians * (180.0 / Math.PI);

    /// <summary>Pointy-top hexagon vertices, starting at the top and running clockwise, rounded to three places like Python.</summary>
    public static IReadOnlyList<Point> Vertices(double radius, Point center)
    {
        var vertices = new Point[6];
        for (var i = 0; i < vertices.Length; i++)
        {
            var angle = Radians((60 * i) - 90);
            vertices[i] = new Point(
                PythonNumber.Round(center.X + (radius * Math.Cos(angle)), 3),
                PythonNumber.Round(center.Y + (radius * Math.Sin(angle)), 3));
        }

        return vertices;
    }
}
