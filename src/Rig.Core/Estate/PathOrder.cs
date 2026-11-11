namespace Rig.Core.Estate;

/// <summary>
/// Orders paths segment by segment, as Python sorts <c>pathlib</c> paths: <c>foo/bar</c> before <c>foo-x</c>, where a
/// plain string comparison puts the <c>-</c> first. Each segment compares ordinally.
/// </summary>
public sealed class PathOrder : IComparer<string>
{
    /// <summary>The one instance.</summary>
    public static readonly PathOrder Instance = new();

    private static readonly char[] Separators = ['/', '\\'];

    private PathOrder()
    {
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var left = Parts(x);
        var right = Parts(y);
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static string[] Parts(string path)
    {
        var separators = OperatingSystem.IsWindows() ? Separators : Separators[..1];
        var parts = path.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return Path.IsPathRooted(path) && path[0] is '/' or '\\' ? ["/", .. parts] : parts;
    }
}
