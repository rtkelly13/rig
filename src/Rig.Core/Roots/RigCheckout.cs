namespace Rig.Core.Roots;

/// <summary>Where the rig checkout is: the folder rig serves the web app, assets and C4 samples from.</summary>
public static class RigCheckout
{
    /// <summary>The environment variable naming the rig checkout.</summary>
    public const string HomeVariable = "RIG_HOME";

    /// <summary>Where the estate clones rig: <c>&lt;code root&gt;/personal/rig</c>.</summary>
    public static string Under(string codeRoot) => Path.Combine(CodeRoot.Personal(codeRoot), "rig");

    /// <summary>The checkout's <c>config/&lt;name&gt;</c>.</summary>
    public static string Config(string checkout, string name) => Path.Join(checkout, "config", name);

    /// <summary>The checkout a build output folder belongs to: the nearest folder above it holding <c>rig.slnx</c> and the CLI project, else null.</summary>
    public static string? FromBuildOutput(string baseDirectory)
    {
        for (var folder = new DirectoryInfo(Path.GetFullPath(baseDirectory)); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx"))
                && File.Exists(Path.Join(folder.FullName, "src", "Rig.Cli", "Rig.Cli.csproj")))
            {
                return folder.FullName;
            }
        }

        return null;
    }
}
