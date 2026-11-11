using Rig.Core.Processes;

namespace Rig.Core.Orca;

/// <summary>
/// <see cref="IOrcaLive"/> over the runtime socket. A null <paramref name="runtime"/> (no usable
/// <c>orca-runtime.json</c>) is an Orca that is not running: every call fails rather than throws.
/// </summary>
/// <param name="runtime">The running Orca's runtime, or null when there is none.</param>
public sealed class OrcaLive(OrcaRuntime? runtime) : IOrcaLive
{
    /// <summary>The runtime for the instance whose metadata is at <paramref name="metadataPath"/>, or null when Orca is not running.</summary>
    public static OrcaRuntime? TryConnect(string metadataPath, IOrcaExchange exchange)
    {
        try
        {
            return OrcaRuntime.FromMetadata(metadataPath, exchange);
        }
        catch (OrcaUnavailableException)
        {
            return null;
        }
    }

    /// <summary>What the running Orca launches each agent with, or null when it could not be asked.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> LaunchArgsAsync(CancellationToken cancellationToken)
    {
        if (runtime is null)
        {
            return null;
        }

        try
        {
            return await runtime.AgentDefaultArgsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OrcaUnavailableException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRepoAsync(string repoPath, RepoUpdates updates, CancellationToken cancellationToken)
    {
        if (runtime is null)
        {
            return false;
        }

        try
        {
            await runtime.UpdateRepoAsync(repoPath, updates, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OrcaUnavailableException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> SendLaunchLinesAsync(IReadOnlyDictionary<string, string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (runtime is null)
        {
            return null;
        }

        try
        {
            var merged = new Dictionary<string, string>(await runtime.AgentDefaultArgsAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
            foreach (var (agent, line) in lines)
            {
                merged[agent] = line;
            }

            return await runtime.SetAgentDefaultArgsAsync(merged, cancellationToken).ConfigureAwait(false);
        }
        catch (OrcaUnavailableException)
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="IOrcaCli"/> through the <c>orca</c> program. With a user-data folder it targets that instance, through
/// <c>ORCA_USER_DATA_PATH</c>.
/// </summary>
/// <param name="proc">Runs the program.</param>
/// <param name="userData">The instance's user-data folder, or null for the default instance.</param>
public sealed class OrcaCliRunner(IProc proc, string? userData) : IOrcaCli
{
    /// <inheritdoc />
    public async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        var request = new ProcRequest(["orca", .. args])
        {
            Environment = userData is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [OrcaLocations.UserDataVariable] = userData },
        };
        var ran = await proc.RunAsync(request, cancellationToken).ConfigureAwait(false);
        return ran.Ok ? ran.Out : null;
    }
}
