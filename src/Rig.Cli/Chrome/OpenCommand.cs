using DotMake.CommandLine;
using Rig.Cli.Commands;
using Rig.Core;

namespace Rig.Cli.Chrome;

/// <summary>
/// <c>rig open URL</c>: opens a page in the "Rig links" Chrome window beside the dashboard, creating it when it is not
/// open, and in the default browser where that cannot work. Exit codes: 0 opened, 2 not an absolute address.
/// </summary>
/// <param name="links">Routes the page.</param>
/// <param name="output">Where the outcome goes.</param>
/// <param name="error">Where a bad address is reported.</param>
[CliCommand(Name = "open", Description = "Open a page in the Rig links Chrome window (Work desktop, screen 3), or the default browser.")]
internal sealed class OpenCommand(RigLinks links, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal OpenCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The page to open.</summary>
    [CliArgument(Name = "url", Description = "An absolute address, such as https://github.com/rtkelly13/rig")]
    public string Address { get; set; } = null!;

    /// <summary>The <c>open</c> command.</summary>
    public System.CommandLine.Command Build() => DeclarativeCommand.Build(this);

    /// <summary>Opens the page and reports where it went.</summary>
    public int Run()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var page))
        {
            error.Write($"rig open: not an absolute address: {Address}\n");
            return (int)ExitCode.Error;
        }

        output.Write(links.Open(page).Detail + "\n");
        return (int)ExitCode.Clean;
    }
}
