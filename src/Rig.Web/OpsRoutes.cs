using Microsoft.AspNetCore.Routing;
using Rig.Core.Processes;
using Rig.Core.ReviewQueue;
using Rig.Web.Ops;

namespace Rig.Web;

/// <summary>
/// The routes the Python web child used to serve: the triage inbox, the estate's ops answers, and telemetry. Ported
/// from <c>rig.triage.web</c>, <c>rig.estate.web</c> and <c>rig.otel_stack.web</c>, so the host no longer forwards them.
/// </summary>
public sealed class OpsRoutes : IDisposable
{
    /// <summary>Groups the three route sources; each is injectable for tests.</summary>
    /// <param name="triage">The persistent triage queue.</param>
    /// <param name="estate">The decisions feed, disk usage and trim cache.</param>
    /// <param name="telemetry">The trace store and explorers.</param>
    public OpsRoutes(TriageInbox triage, EstateOps estate, Telemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(estate);
        ArgumentNullException.ThrowIfNull(telemetry);
        Triage = triage;
        Estate = estate;
        Telemetry = telemetry;
    }

    /// <summary>The persistent triage queue.</summary>
    public TriageInbox Triage { get; }

    /// <summary>The estate's ops answers.</summary>
    public EstateOps Estate { get; }

    /// <summary>The trace store and explorers.</summary>
    public Telemetry Telemetry { get; }

    /// <summary>
    /// The real sources for a host serving <paramref name="settings"/>: SQLite and <c>gh</c> for triage, git and the
    /// decision feed for the estate, ClickHouse and the day files for telemetry.
    /// </summary>
    public static OpsRoutes ForSettings(WebHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var proc = new SystemProc();
        var git = new Git(proc, settings.EstateRoot);
        var signals = AgentSignals.ForEstate(settings.RegistryFile, settings.EstateRoot, git);
        var triage = new TriageInbox(settings.DbPath, new QueueGitHub(new GhApi(proc)), settings.QueuePath, signals);
        return new(triage, EstateOps.ForSettings(settings), Telemetry.ForTraces(settings.TracesDir));
    }

    /// <summary>Maps every route group onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoints go.</param>
    /// <param name="registry">The managed-projects registry a triage refresh reads.</param>
    public void Map(IEndpointRouteBuilder routes, string registry)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrEmpty(registry);
        TriageRoutes.Map(routes, Triage, registry);
        EstateOpsRoutes.Map(routes, Estate);
        TelemetryRoutes.Map(routes, Telemetry);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Triage.Dispose();
        Estate.Dispose();
        Telemetry.Dispose();
    }
}
