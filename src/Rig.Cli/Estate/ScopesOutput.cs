using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Workspace;

namespace Rig.Cli.Estate;

/// <summary>How <c>rig estate scopes</c> prints: the role, the configured owners on each side, and the owners the clones belong to.</summary>
internal static class ScopesOutput
{
    /// <summary>For a machine, such as the daily orchestrator deciding what it may write to.</summary>
    public static JsonObject Json(OwnerScopes scopes, string root, IReadOnlyList<OwnerTally> owners, IReadOnlyList<string> unowned) => new()
    {
        ["root"] = root,
        ["role"] = Name(scopes.Role),
        ["role_set"] = scopes.RoleSet,
        ["personal"] = Strings(scopes.Personal),
        ["work"] = Strings(scopes.Work),
        ["writable"] = Strings(scopes.Writable),
        ["owners"] = new JsonArray([.. owners.Select(tally => (JsonNode)new JsonObject
        {
            ["owner"] = tally.Owner,
            ["class"] = Name(tally.Class),
            ["writable"] = tally.Writable,
            ["clones"] = Strings(tally.Clones),
        })]),
        ["unowned"] = Strings(unowned),
    };

    /// <summary>For a person: the role, then one line per owner found, marked with its side and whether it is written to.</summary>
    public static string Text(OwnerScopes scopes, IReadOnlyList<OwnerTally> owners, IReadOnlyList<string> unowned)
    {
        var text = new StringBuilder();
        var source = scopes.RoleSet ? MachineConfig.RoleKey : $"default; set {MachineConfig.RoleKey}";
        _ = text.Append(CultureInfo.InvariantCulture, $"Role: {Name(scopes.Role)} ({source})");
        _ = text.Append(CultureInfo.InvariantCulture, $"\nPersonal: {List(scopes.Personal)} (config/estate-scopes.json)");
        _ = text.Append(CultureInfo.InvariantCulture, $"\nWork:     {List(scopes.Work)} ({MachineConfig.WorkOrgsKey})");
        _ = text.Append(CultureInfo.InvariantCulture, $"\nWrites to: {List(scopes.Writable)}");
        if (owners.Count > 0)
        {
            _ = text.Append("\n\nOwners of the clones under the root:");
            var width = owners.Max(tally => tally.Owner.Length);
            foreach (var tally in owners)
            {
                var mark = tally.Writable ? "  writes" : string.Empty;
                _ = text.Append(CultureInfo.InvariantCulture, $"\n  {tally.Owner.PadRight(width)}  {Name(tally.Class),-8}  {tally.Clones.Count,3} clone(s){mark}");
            }
        }

        if (unowned.Count > 0)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"\n  ({unowned.Count} clone(s) with no GitHub origin)");
        }

        return text.ToString();
    }

    private static string Name<T>(T value)
        where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static string List(IReadOnlyList<string> names) => names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);
}
