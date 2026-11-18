using System.Security.Cryptography;
using System.Text;

namespace Rig.Image.Tests;

/// <summary>The frozen Python simplifier oracle (tools/image-drift/oracle/dump_oracle.py --simplifier-out).</summary>
internal static class SimplifierOracleFixture
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "SimplifierOracle");

    /// <summary>The parsed simplifier-oracle.json.</summary>
    public static StrictJson Document { get; } = Read();

    /// <summary>A file frozen under SimplifierOracle, such as a Python final document.</summary>
    public static string Text(string relative) => File.ReadAllText(Path.Combine(Folder, relative));

    /// <summary>A repository file by its repo-relative path; the oracle's inputs live in the assets and the score oracle.</summary>
    public static string RepoText(string relative) => File.ReadAllText(Path.Combine(OracleFixture.RepoRoot, relative));

    /// <summary>
    /// Compares a produced document with a frozen text-or-hash entry and returns a description of the difference, or null when they
    /// match. Documents over the fixture's size limit are frozen as a SHA-256 and a length only.
    /// </summary>
    public static string? Difference(string label, StrictJson frozen, string actual)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
        var length = frozen.Property("length").AsInteger();
        if (frozen.PropertyNames().Contains("text"))
        {
            var expected = frozen.Property("text").AsString();
            return expected == actual ? null : $"{label}: expected {Quote(expected)} but found {Quote(actual)}";
        }

        var expectedHash = frozen.Property("sha256").AsString();
        return expectedHash == hash ? null : $"{label}: expected sha256 {expectedHash} (length {length}) but found {hash} (length {actual.Length})";
    }

    private static string Quote(string text) => text.Length <= 600 ? $"'{text}'" : $"'{text[..300]}...{text[^300..]}' ({text.Length} chars)";

    private static StrictJson Read()
    {
        var path = Path.Combine(Folder, "simplifier-oracle.json");
        return StrictJson.Parse(path, File.ReadAllText(path));
    }
}
