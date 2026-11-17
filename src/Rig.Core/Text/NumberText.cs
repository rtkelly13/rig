using System.Globalization;

namespace Rig.Core.Text;

/// <summary>Numbers as invariant text.</summary>
internal static class NumberText
{
    /// <summary>Round half to even on the exact value. F0 is exact; F1 matches outside exact ties.</summary>
    public static string Fixed(double value, int digits) =>
        digits == 0
            ? Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString($"F{digits}", CultureInfo.InvariantCulture);
}
