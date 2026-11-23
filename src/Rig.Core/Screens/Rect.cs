using System.Text.Json.Nodes;

namespace Rig.Core.Screens;

/// <summary>A screen rectangle in pixels.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public sealed record Rect(int Left, int Top, int Width, int Height)
{
    /// <summary>The right edge, one past the last column.</summary>
    public int Right => Left + Width;

    /// <summary>The bottom edge, one past the last row.</summary>
    public int Bottom => Top + Height;

    /// <summary>The horizontal centre, rounded down.</summary>
    public int CenterX => Left + FloorHalf(Width);

    /// <summary>The vertical centre, rounded down.</summary>
    public int CenterY => Top + FloorHalf(Height);

    /// <summary>The rectangle as a plan file holds it.</summary>
    public JsonObject ToJson() => new() { ["left"] = Left, ["top"] = Top, ["width"] = Width, ["height"] = Height };

    private static int FloorHalf(int value) => (int)Math.Floor(value / 2.0);
}
