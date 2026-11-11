using Rig.Core.Policy;

namespace Rig.Core.Tests.Policy;

/// <summary>The checked-in estate policy, and scratch policies written for a test.</summary>
public static class PolicyFiles
{
    /// <summary>The checkout's <c>config/cli-allowlists.json</c>.</summary>
    public static string Real { get; } = Path.Join(Path.GetDirectoryName(RepoTemp.Root), "config", "cli-allowlists.json");

    /// <summary>The checked-in policy, loaded as on macOS and Linux.</summary>
    public static CommandPolicy Load() => CommandPolicy.Load(Real, windows: false);

    /// <summary>Writes <paramref name="json"/> as <c>policy.json</c> in <paramref name="folder"/> and returns its path.</summary>
    public static string Write(string folder, string json)
    {
        var path = Path.Join(folder, "policy.json");
        File.WriteAllText(path, json);
        return path;
    }
}
