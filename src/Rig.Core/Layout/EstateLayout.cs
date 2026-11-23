using Rig.Core.Jobs;
using Rig.Core.Roots;
using Rig.Core.Runtime;

namespace Rig.Core.Layout;

/// <summary>What <see cref="EstateLayout.Resolve"/> reads: the environment, where the running rig was loaded from, the home and app-data folders.</summary>
/// <param name="Environment">Reads an environment variable (<c>CODE_ROOT</c>, <c>RIG_HOME</c>).</param>
/// <param name="BaseDirectory">The folder the running assembly was loaded from.</param>
/// <param name="Home">The home folder.</param>
/// <param name="AppData">rig's app-data folder, holding the installed runtime copy.</param>
public sealed record EstateInputs(Func<string, string?> Environment, string BaseDirectory, string Home, string AppData)
{
    /// <summary>This process's inputs.</summary>
    public static EstateInputs Current() => new(
        System.Environment.GetEnvironmentVariable,
        AppContext.BaseDirectory,
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        Roots.AppData.Current());
}

/// <summary>
/// Where this machine's estate is, resolved once and passed down: the code root, the rig checkout rig reads its config
/// from, and the git checkout it was built from. Every path derived from them (<see cref="Personal"/>,
/// <see cref="ScopesFile"/>, <see cref="Config"/>) comes from here, so there is one answer per process.
/// </summary>
/// <param name="Home">The home folder the layout was resolved for.</param>
/// <param name="CodeRoot">The folder every repo is cloned into; see <see cref="Roots.CodeRoot"/> for the order.</param>
/// <param name="Checkout">The rig checkout whose <c>config/</c>, assets and <c>temp/</c> this process uses.</param>
/// <param name="Source">The git checkout this rig was built from, when it can tell.</param>
public sealed record EstateLayout(string Home, string CodeRoot, string Checkout, string? Source)
{

    /// <summary>The code root's <c>personal/</c> folder.</summary>
    public string Personal => Roots.CodeRoot.Personal(CodeRoot);

    /// <summary>The checkout's <c>config/estate-scopes.json</c>, naming the org folders beside <see cref="Personal"/>.</summary>
    public string ScopesFile => Config(EstateScopes.FileName);

    /// <summary>The checkout's <c>config/&lt;name&gt;</c>.</summary>
    public string Config(string name) => RigCheckout.Config(Checkout, name);

    /// <summary><paramref name="path"/> as people type it: <c>~/…</c> with forward slashes when it is inside <see cref="Home"/>.</summary>
    public string Display(string path) => Roots.CodeRoot.Display(path, Home);

    /// <summary>This process's layout.</summary>
    public static EstateLayout Current() => Resolve(EstateInputs.Current());

    /// <summary>
    /// The layout under <paramref name="home"/> with nothing else to go on: no environment and no running checkout, so
    /// the code root is <c>&lt;home&gt;/code</c> (or the drive's). What a host uses when it was not given a layout.
    /// </summary>
    public static EstateLayout Isolated(string home) => Resolve(IsolatedInputs(home));

    /// <summary>
    /// Resolves the layout. The source checkout is <c>RIG_HOME</c>, else the build output's checkout, else the jobs
    /// runtime's, else the one the installed runtime copy recorded. The code root then follows <see cref="Roots.CodeRoot.Resolve"/>
    /// given that checkout. The checkout rig reads is <c>RIG_HOME</c>, else the runtime copy it runs from (so its jobs
    /// use that copy), else the jobs runtime's, else <c>&lt;root&gt;/personal/rig</c>.
    /// </summary>
    public static EstateLayout Resolve(EstateInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var rigHome = inputs.Environment(RigCheckout.HomeVariable);
        var configured = string.IsNullOrEmpty(rigHome) ? null : rigHome;
        var jobs = JobsSite.PublishedCheckout(inputs.BaseDirectory);
        var source = configured
            ?? RigCheckout.FromBuildOutput(inputs.BaseDirectory)
            ?? jobs
            ?? RuntimeManager.RecordedRepo(RuntimeManager.RuntimeRoot(inputs.AppData));
        var root = Roots.CodeRoot.Resolve(inputs.Environment(Roots.CodeRoot.EnvironmentVariable), inputs.BaseDirectory, inputs.Home, source);
        var checkout = configured ?? JobsSite.CopyOf(inputs.BaseDirectory, inputs.AppData) ?? jobs ?? RigCheckout.Under(root);
        return new EstateLayout(inputs.Home, root, checkout, source);
    }

    /// <summary>
    /// The layout for <paramref name="home"/>, for a command given <c>--home</c>: this one when it is the same home,
    /// otherwise one resolved from that home alone, ignoring the environment, the running checkout and any marked
    /// root above it, so a temporary home never writes into the real estate.
    /// </summary>
    public EstateLayout ForHome(string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return PathResolution.Same(home, Home) ? this : Isolated(home);
    }

    // Inputs that see nothing outside home: no environment, the search starts at home, and app data that does not exist.
    private static EstateInputs IsolatedInputs(string home) => new(_ => null, home, home, Path.Join(home, ".rig-isolated"));
}
