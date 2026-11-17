namespace Rig.Core.Roots;

/// <summary>Where the rig checkout is: the folder rig serves the web app, assets and C4 samples from.</summary>
public static class RigCheckout
{
    /// <summary>The environment variable naming the rig checkout.</summary>
    public const string HomeVariable = "RIG_HOME";

    /// <summary>
    /// The rig checkout: <paramref name="configured"/> (<c>RIG_HOME</c>) when set, else <c>&lt;code root&gt;/personal/rig</c>,
    /// where the estate clones it.
    /// </summary>
    public static string Resolve(string? configured, string codeRoot) =>
        string.IsNullOrEmpty(configured) ? Path.Combine(codeRoot, CodeRoot.PersonalFolder, "rig") : configured;
}
