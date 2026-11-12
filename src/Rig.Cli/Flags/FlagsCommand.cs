using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Flags;

namespace Rig.Cli.Flags;

/// <summary>
/// <c>rig flags</c>: list the feature flags and store overrides in <c>rig.db</c>. Ported from <c>rig.flags.cli</c>;
/// messages, unknown-flag errors included, go to stdout as they did there.
/// </summary>
internal sealed class FlagsCommand(FlagStore store, TextWriter output)
{
    /// <summary>A flag name that is not declared exits with this.</summary>
    public const int UnknownExit = 1;

    /// <summary>The <c>flags</c> command.</summary>
    public Command Build()
    {
        var list = new Command("list", "Show every flag, its default, any stored override and the effective state.");
        list.SetAction(_ => List());

        return new Command("flags", "Feature flags shared across the rig stack (defaults in code, overrides in rig.db).")
        {
            list,
            Setter("enable", "Store an override switching the flag on.", enabled: true),
            Setter("disable", "Store an override switching the flag off.", enabled: false),
            NameCommand("reset", "Remove the stored override so the default applies.", name =>
                $"{name}: {(store.Clear(name) ? "override removed" : "no override stored")}"),
        };
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private int List()
    {
        var table = new TextTable(
            null,
            [("Flag", false), ("Effective", false), ("Default", false), ("Override", false), ("Description", false)]);
        foreach (var state in store.States())
        {
            table.Add(
                state.Flag.Name,
                OnOff(state.Enabled),
                OnOff(state.Flag.Default),
                state.Override is { } on ? OnOff(on) : "-",
                state.Flag.Description);
        }

        output.Write(table.Render());
        return 0;
    }

    private Command Setter(string verb, string description, bool enabled) =>
        NameCommand(verb, description, name =>
        {
            store.Set(name, enabled);
            return $"{name}: {OnOff(enabled)} (stored override)";
        });

    private Command NameCommand(string verb, string description, Func<string, string> run)
    {
        var name = new Argument<string>("name") { Description = "Flag name" };
        var command = new Command(verb, description) { name };
        command.SetAction(parsed =>
        {
            try
            {
                output.Write(run(parsed.GetRequiredValue(name)) + "\n");
                return 0;
            }
            catch (UnknownFlagException e)
            {
                output.Write(e.Message + "\n");
                return UnknownExit;
            }
        });
        return command;
    }
}
