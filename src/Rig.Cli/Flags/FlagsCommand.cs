using System.CommandLine;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core;
using Rig.Core.Flags;

namespace Rig.Cli.Flags;

/// <summary>
/// <c>rig flags</c>: list the feature flags and store overrides in <c>rig.db</c>. Ported from <c>rig.flags.cli</c>;
/// messages, unknown-flag errors included, go to stdout as they did there.
/// </summary>
/// <param name="store">The stored overrides, read and written here.</param>
/// <param name="output">Where every message goes.</param>
/// <param name="error">Where the missing-subcommand error goes.</param>
[CliCommand(Name = "flags", Description = "Feature flags shared across the rig stack (defaults in code, overrides in rig.db).")]
internal sealed class FlagsCommand(FlagStore store, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal FlagsCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>A flag name that is not declared exits with this.</summary>
    public const int UnknownExit = 1;

    /// <summary>The <c>flags</c> command.</summary>
    public Command Build()
    {
        var flags = DeclarativeCommand.Build(this);
        flags.Add(DeclarativeCommand.Build(new ListCommand(store, output)));
        flags.Add(DeclarativeCommand.Build(new EnableCommand(store, output)));
        flags.Add(DeclarativeCommand.Build(new DisableCommand(store, output)));
        flags.Add(DeclarativeCommand.Build(new ResetCommand(store, output)));
        return flags;
    }

    /// <summary>A group named without one of its verbs is the parser's error, as before.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>One flag name, taken by <c>enable</c>, <c>disable</c> and <c>reset</c>.</summary>
    internal abstract class NamedCommand(FlagStore store, TextWriter output)
    {
        /// <summary>The declared flag to act on.</summary>
        [CliArgument(Name = "name", Description = "Flag name")]
        public string Name { get; set; } = null!;

        /// <summary>The stored overrides.</summary>
        protected FlagStore Store { get; } = store;

        /// <summary>Where the message goes.</summary>
        protected TextWriter Output { get; } = output;

        /// <summary>Writes what <paramref name="run"/> returned, or the unknown-flag error, and exits as Python did.</summary>
        protected int Report(Func<string, string> run)
        {
            try
            {
                Output.Write(run(Name) + "\n");
                return (int)ExitCode.Clean;
            }
            catch (UnknownFlagException e)
            {
                Output.Write(e.Message + "\n");
                return UnknownExit;
            }
        }
    }

    /// <summary><c>rig flags list</c>.</summary>
    [CliCommand(Name = "list", Description = "Show every flag, its default, any stored override and the effective state.")]
    internal sealed class ListCommand(FlagStore store, TextWriter output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ListCommand()
            : this(null!, TextWriter.Null)
        {
        }

        /// <summary>The table: one row per declared flag.</summary>
        public int Run()
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
            return (int)ExitCode.Clean;
        }
    }

    /// <summary><c>rig flags enable</c>.</summary>
    [CliCommand(Name = "enable", Description = "Store an override switching the flag on.")]
    internal sealed class EnableCommand(FlagStore store, TextWriter output) : NamedCommand(store, output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal EnableCommand()
            : this(null!, TextWriter.Null)
        {
        }

        /// <summary>Stores the override and says what it stored.</summary>
        public int Run() => Report(name =>
        {
            Store.Set(name, true);
            return $"{name}: on (stored override)";
        });
    }

    /// <summary><c>rig flags disable</c>.</summary>
    [CliCommand(Name = "disable", Description = "Store an override switching the flag off.")]
    internal sealed class DisableCommand(FlagStore store, TextWriter output) : NamedCommand(store, output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DisableCommand()
            : this(null!, TextWriter.Null)
        {
        }

        /// <summary>Stores the override and says what it stored.</summary>
        public int Run() => Report(name =>
        {
            Store.Set(name, false);
            return $"{name}: off (stored override)";
        });
    }

    /// <summary><c>rig flags reset</c>.</summary>
    [CliCommand(Name = "reset", Description = "Remove the stored override so the default applies.")]
    internal sealed class ResetCommand(FlagStore store, TextWriter output) : NamedCommand(store, output)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ResetCommand()
            : this(null!, TextWriter.Null)
        {
        }

        /// <summary>Clears the override and says whether there was one.</summary>
        public int Run() => Report(name =>
            $"{name}: {(Store.Clear(name) ? "override removed" : "no override stored")}");
    }
}
