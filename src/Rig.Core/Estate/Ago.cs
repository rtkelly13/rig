using System.Globalization;

namespace Rig.Core.Estate;

/// <summary>An age as the estate commands print it: minutes under an hour, hours under two days, then days.</summary>
public static class Ago
{
    /// <summary><c>12m ago</c>, <c>3h ago</c> or <c>4d ago</c>; <c>just now</c> when there is no age.</summary>
    public static string Format(TimeSpan? age)
    {
        if (age is not { } delta)
        {
            return "just now";
        }

        var hours = (long)Math.Floor(delta.TotalHours);
        return hours switch
        {
            < 1 => string.Create(CultureInfo.InvariantCulture, $"{(long)Math.Floor(delta.TotalMinutes)}m ago"),
            < 48 => string.Create(CultureInfo.InvariantCulture, $"{hours}h ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{hours / 24}d ago"),
        };
    }
}
