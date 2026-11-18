namespace Rig.Image.Pathways;

/// <summary>One declared brand mark variant (image_drift's <c>Variant</c>, minus the wood-field knobs only retired builders used).</summary>
/// <param name="Name">The label shown in listings.</param>
/// <param name="Description">What the variant is for.</param>
public sealed record Variant(string Name, string Description);

/// <summary>Raised when a builder is asked for a variant it does not declare.</summary>
public sealed class UnknownVariantException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public UnknownVariantException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was asked for and what is declared.</param>
    public UnknownVariantException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">What was asked for and what is declared.</param>
    /// <param name="innerException">The cause.</param>
    public UnknownVariantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>image_drift's <c>resolve_variant</c>: an unknown key is an error, never a silent fallback.</summary>
public static class VariantResolution
{
    /// <summary>Resolves a requested key against a builder's declared variants; null takes <paramref name="fallback"/>.</summary>
    public static string Resolve(string? variant, IReadOnlyCollection<string> declared, string fallback)
    {
        var chosen = variant ?? fallback;
        if (!declared.Contains(chosen))
        {
            var known = string.Join(", ", declared.Order(StringComparer.Ordinal));
            throw new UnknownVariantException($"Unknown variant '{chosen}'. Declared variants: {known}");
        }

        return chosen;
    }
}

/// <summary>Python's float formatting where the port prints a double with <c>str()</c>.</summary>
internal static class PyFloat
{
    /// <summary>Python's <c>repr(float)</c> for the magnitudes the builders print: shortest round-trip digits, always with a point.</summary>
    public static string Repr(double value)
    {
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.Ordinal))
        {
            throw new NotSupportedException($"{text} needs Python's exponent form, which the builders never print");
        }

        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }
}
