namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// The checked-in placement of every area in <c>Rig.Core</c> and <c>Rig.Cli</c>. A new area fails
/// <c>StructureTests</c> until it is listed here, so placing it is a reviewed decision rather than a default.
/// Workspace and WorkItems are features because they run git or call GitHub. Orca is an adapter: it wraps the Orca
/// runtime and features lean on it.
/// </summary>
internal static class Roles
{
    public static readonly RoleMap Core = new("Rig.Core", new Dictionary<string, Role>
    {
        [""] = Role.Leaf,
        ["CommandSurface"] = Role.Leaf,
        ["Config"] = Role.Leaf,
        ["Files"] = Role.Leaf,
        ["Flags"] = Role.Leaf,
        ["Markup"] = Role.Leaf,
        ["Roots"] = Role.Leaf,
        ["Storage"] = Role.Leaf,
        ["Text"] = Role.Leaf,

        ["Policy"] = Role.Domain,
        ["Reconciliation"] = Role.Domain,
        ["Screens"] = Role.Domain,
        ["Tracing"] = Role.Domain,

        ["Browser"] = Role.Adapter,
        ["GitHub"] = Role.Adapter,
        ["Orca"] = Role.Adapter,
        ["Paket"] = Role.Adapter,
        ["Processes"] = Role.Adapter,
        ["Services"] = Role.Adapter,
        ["Windows"] = Role.Adapter,
        ["Worktrees"] = Role.Adapter,
        ["Wrapups"] = Role.Adapter,
        ["Chrome.MacOS"] = Role.Adapter,
        ["Chrome.Windows"] = Role.Adapter,
        ["Desktop.Windows"] = Role.Adapter,

        ["C4"] = Role.Feature,
        ["Chrome"] = Role.Feature,
        ["Connectivity"] = Role.Feature,
        ["Desktop"] = Role.Feature,
        ["Devenv"] = Role.Feature,
        ["Estate"] = Role.Feature,
        ["Governance"] = Role.Feature,
        ["Jobs"] = Role.Feature,
        ["Mail"] = Role.Feature,
        ["Observability"] = Role.Feature,
        ["Otel"] = Role.Feature,
        ["Packages"] = Role.Feature,
        ["ReviewQueue"] = Role.Feature,
        ["Runtime"] = Role.Feature,
        ["Skills"] = Role.Feature,
        ["Tools"] = Role.Feature,
        ["Vault"] = Role.Feature,
        ["Web"] = Role.Feature,
        ["WorkItems"] = Role.Feature,
        ["Workspace"] = Role.Feature,
    });

    public static readonly RoleMap Cli = new("Rig.Cli", new Dictionary<string, Role>
    {
        [""] = Role.CompositionRoot,
        ["Commands"] = Role.Leaf,
        ["Forwarding"] = Role.Leaf,
        ["Rendering"] = Role.Leaf,

        ["Agent"] = Role.Feature,
        ["Browser"] = Role.Feature,
        ["C4"] = Role.Feature,
        ["Chrome"] = Role.Feature,
        ["Desktop"] = Role.Feature,
        ["Devenv"] = Role.Feature,
        ["Estate"] = Role.Feature,
        ["Flags"] = Role.Feature,
        ["Gh"] = Role.Feature,
        ["Governance"] = Role.Feature,
        ["Image"] = Role.Feature,
        ["Jobs"] = Role.Feature,
        ["Latex"] = Role.Feature,
        ["Mail"] = Role.Feature,
        ["Models"] = Role.Feature,
        ["Otel"] = Role.Feature,
        ["Policy"] = Role.Feature,
        ["ReviewQueue"] = Role.Feature,
        ["Runtime"] = Role.Feature,
        ["Skills"] = Role.Feature,
        ["Status"] = Role.Feature,
        ["Tools"] = Role.Feature,
        ["Traces"] = Role.Feature,
        ["Vault"] = Role.Feature,
        ["Web"] = Role.Feature,
        ["Work"] = Role.Feature,
        ["Workspace"] = Role.Feature,
    });
}
