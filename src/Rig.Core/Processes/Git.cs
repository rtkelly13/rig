using Rig.Core.Roots;

namespace Rig.Core.Processes;

/// <summary>
/// Runs <c>git</c>, hardened when the working directory lies under <c>&lt;code root&gt;/external</c> or
/// <c>&lt;code root&gt;/other</c>. Those folders hold code nobody here wrote or reviewed, and a clone's own config
/// can run programs: hooks, an fsmonitor command, <c>ext::</c> transports, submodule recursion. The hardening turns
/// each off for the one call and stops git prompting for credentials. Other protocols stay, so fetch still works.
/// </summary>
/// <param name="proc">The process runner.</param>
/// <param name="codeRoot">The code root whose untrusted folders get the hardening.</param>
public sealed class Git(IProc proc, string codeRoot)
{
    /// <summary>The default timeout for one git call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Folders under the code root whose clones are untrusted (see the code root's AGENTS.md).</summary>
    public static readonly IReadOnlyList<string> UntrustedFolders = ["external", "other"];

    /// <summary>Prepended to every git call under an untrusted folder.</summary>
    public static readonly IReadOnlyList<string> HardeningArgs =
    [
        "-c", $"core.hooksPath={NullDevice}",
        "-c", "core.fsmonitor=false",
        "-c", "protocol.ext.allow=never",
        "-c", "submodule.recurse=false",
    ];

    /// <summary>Added to the environment of every git call under an untrusted folder.</summary>
    public static readonly IReadOnlyDictionary<string, string> HardeningEnvironment =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>Whether <paramref name="path"/> (default: the working directory) is under an untrusted folder.</summary>
    public bool IsUntrusted(string? path)
    {
        var target = PathResolution.Resolve(path ?? Directory.GetCurrentDirectory());
        var root = PathResolution.Resolve(codeRoot);
        // Each folder is resolved too: IsUnder compares resolved paths, and a symlinked external/ must stay untrusted.
        return UntrustedFolders.Any(folder => PathResolution.IsUnder(target, PathResolution.Resolve(Path.Combine(root, folder))));
    }

    /// <summary>Runs <c>git <paramref name="args"/></c> in <paramref name="workingDirectory"/>.</summary>
    public Task<Ran> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? stdinText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var untrusted = IsUntrusted(workingDirectory);
        var env = new Dictionary<string, string>(environment ?? new Dictionary<string, string>());
        if (untrusted)
        {
            foreach (var (name, value) in HardeningEnvironment)
            {
                env[name] = value;
            }
        }

        IReadOnlyList<string> argv = untrusted ? ["git", .. HardeningArgs, .. args] : ["git", .. args];
        return proc.RunAsync(
            new ProcRequest(argv)
            {
                WorkingDirectory = workingDirectory,
                Timeout = timeout ?? DefaultTimeout,
                Environment = env,
                StdinText = stdinText,
            },
            cancellationToken);
    }

    /// <summary>What <c>git <paramref name="args"/></c> prints, stripped; empty when it fails.</summary>
    public async Task<string> OutAsync(string? workingDirectory, params string[] args) =>
        (await RunAsync(workingDirectory, args).ConfigureAwait(false)).OutOrEmpty;

    /// <summary>The non-empty, stripped lines <c>git <paramref name="args"/></c> prints, or null when it fails.</summary>
    public async Task<IReadOnlyList<string>?> LinesAsync(string? workingDirectory, params string[] args) =>
        (await RunAsync(workingDirectory, args).ConfigureAwait(false)).Lines;
}
