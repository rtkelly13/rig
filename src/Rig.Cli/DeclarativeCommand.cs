using System.CommandLine;
using DotMake.CommandLine;
using Rig.Core;

namespace Rig.Cli;

/// <summary>
/// Turns one DotMake declaration (ADR 0023) into the command rig composes. rig's root is still stitched in
/// <see cref="RigApp"/> by hand, so a declaration is built as one named sub-command — never a process root, which would
/// take its name from the executable — and bound to the instance the composition root wired with its services.
/// </summary>
internal static class DeclarativeCommand
{
    // Registration is global to DotMake, and tests build their commands in parallel.
    private static readonly Lock Gate = new();

    /// <summary>The command <paramref name="definition"/> declares, bound to that instance and named by its attribute.</summary>
    /// <typeparam name="T">The declaration: a class carrying <see cref="CliCommandAttribute"/>.</typeparam>
    internal static Command Build<T>(T definition)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (Gate)
        {
            // Built as a sub-command, not a process root: a root takes its name from the executable, not the attribute.
            CliCommandBuilder.RegisterAsChild(typeof(DeclarativeCommand), typeof(T));

            // DotMake notes each command's name on its parent namer, so the namer is fresh per build: rig builds its
            // tree more than once (once to decide dispatch and once to run) and a shared namer would call the second
            // one a name clash.
            var anchor = new Anchor();
            var context = new CliBindingContext();
            context.NamerMap[anchor] = new CliNamer(
                CliNameAutoGenerate.None,
                CliNameCasingConvention.KebabCase,
                CliNamePrefixConvention.DoubleHyphen,
                CliNameAutoGenerate.None,
                CliNamePrefixConvention.SingleHyphen,
                null);
            var command = CliCommandBuilder.Get<T>().BuildWithParent(context, anchor);
            // DotMake creates its own instance to bind onto; rig's instance already holds its services.
            context.CreatorMap[typeof(T)] = () => definition;
            return command;
        }
    }

    // The anchor only lends its namer to the declaration built under it. Each command gets a fresh one so that two
    // independent builds of the same tree — RigApp builds one to decide dispatch and one to run — never conflict.
    private sealed class Anchor : CliCommandBuilder
    {
        protected override Command DoBuild(CliBindingContext bindingContext) =>
            throw new NotSupportedException("The naming anchor builds no command of its own.");
    }

    /// <summary>
    /// The group <paramref name="definition"/> declares. A group has no action of its own, so naming it without one of its
    /// subcommands is the parser's own error: the message, and exit code 2.
    /// </summary>
    /// <typeparam name="T">The declaration: a class carrying <see cref="CliCommandAttribute"/>.</typeparam>
    /// <param name="definition">The wired instance.</param>
    /// <returns>The built group.</returns>
    internal static Command BuildGroup<T>(T definition)
        where T : class
    {
        var group = Build(definition);
        group.Action = null;
        return group;
    }

    /// <summary>
    /// Gives <paramref name="command"/>'s option <paramref name="option"/> the default <paramref name="value"/> returns. A
    /// default that depends on what the composition root wired cannot be a property initializer: DotMake copies the
    /// initializer into its generated builder, where the declaration's constructor parameters are out of scope.
    /// </summary>
    /// <typeparam name="TValue">The option's value type.</typeparam>
    /// <param name="command">The built command.</param>
    /// <param name="option">The option's name, as declared.</param>
    /// <param name="value">Produces the default each time the command line is parsed.</param>
    /// <returns><paramref name="command"/>.</returns>
    internal static Command WithDefault<TValue>(this Command command, string option, Func<TValue> value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(value);
        command.Options.OfType<Option<TValue>>().Single(declared => declared.Name == option).DefaultValueFactory = _ => value();
        return command;
    }

    /// <summary>Limits <paramref name="command"/>'s option <paramref name="option"/> to <paramref name="values"/>, as the parser's own check does.</summary>
    /// <param name="command">The built command.</param>
    /// <param name="option">The option's name, as declared.</param>
    /// <param name="values">The accepted values.</param>
    /// <returns><paramref name="command"/>.</returns>
    internal static Command AcceptOnlyFromAmong(this Command command, string option, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(values);
        command.Options.OfType<Option<string>>().Single(declared => declared.Name == option).AcceptOnlyFromAmong([.. values]);
        return command;
    }

    /// <summary>
    /// What a command group prints when it is named without one of its subcommands. DotMake always gives a declared
    /// command an action, so a group keeps the parser's own answer — the message, and exit code 2 — in its handler.
    /// </summary>
    /// <param name="error">rig's error writer.</param>
    /// <returns><see cref="ExitCode.Error"/>.</returns>
    internal static int RequireSubcommand(TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(error);
        error.WriteLine("rig: Required command was not provided.");
        error.WriteLine("Run 'rig --help' for the commands.");
        return (int)ExitCode.Error;
    }
}
