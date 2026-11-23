#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Globalization;
using Rig.Core.Chrome.Extension;

namespace Rig.Cli.Chrome;

/// <summary>
/// <c>rig chrome-tabs extension</c>, on Linux: installs the extension the driver works through, reports whether each
/// profile's copy is connected, and is the native host Chrome starts (<c>host</c>). Chrome starts the host as
/// <c>rig chrome-extension://…/</c>; <see cref="HostArguments"/> turns that into <c>host</c>.
/// </summary>
/// <param name="installer">Puts the extension and host manifests in place.</param>
/// <param name="bridge">Reaches the running hosts.</param>
/// <param name="source">The extension folder shipped beside rig.</param>
/// <param name="socketFolder">Where hosts put their sockets.</param>
/// <param name="output">Where every line goes, except the host's, whose stdout is Chrome's pipe.</param>
internal sealed class ChromeExtensionCommand(ExtensionInstaller installer, IExtensionBridge bridge, string source, string socketFolder, TextWriter output)
{
    /// <summary>The arguments rig runs with: <c>chrome-tabs extension host</c> when Chrome started it as a host.</summary>
    public static string[] HostArguments(string[] args) =>
        args is [var origin, ..] && origin.StartsWith("chrome-extension://", StringComparison.Ordinal)
            ? [ChromeTabsCommand.Name, "extension", "host"]
            : args;

    /// <summary>The command.</summary>
    public Command Build()
    {
        var install = new Command("install", "Copy the extension to ~/.local/share/rig/chrome-extension, register its native host, and add it to Chromium's --load-extension flags. [Linux only]");
        var hostProgram = new Option<string?>("--host-program")
        {
            Description = "The program Chrome starts as the native host. Defaults to rig on PATH, so a runtime update needs no reinstall; name a development build's rig to try it.",
        };
        install.Options.Add(hostProgram);
        install.SetAction(parsed => Print((parsed.GetValue(hostProgram) is { Length: > 0 } program ? installer.WithHost(Path.GetFullPath(program)) : installer).Install(source)));
        var uninstall = new Command("uninstall", "Remove the extension, its host manifests and its --load-extension entry. [Linux only]");
        uninstall.SetAction(_ => Print(installer.Uninstall()));
        var status = new Command("status", "Whether the extension is installed, and which profiles have it connected now. [Linux only]");
        status.SetAction(_ => Status());
        var host = new Command("host", "The native-messaging host Chrome starts for the extension; not for running by hand.") { Hidden = true };
        host.SetAction((_, cancellationToken) => HostAsync(cancellationToken));
        return new Command("extension", "The Chrome extension the Linux driver works through: install, uninstall, status. [Linux only]")
        {
            install, uninstall, status, host,
        };
    }

    private int Print(InstallReport report)
    {
        foreach (var line in report.Lines)
        {
            output.WriteLine(line);
        }

        return 0;
    }

    private int Status()
    {
        _ = Print(installer.Check());
        var connections = bridge.Connections();
        if (connections.Count == 0)
        {
            output.WriteLine("Connected: none (is Chromium running with the extension loaded?)");
            return 1;
        }

        foreach (var connection in connections)
        {
            var who = connection.Email.Length > 0 ? connection.Email : "(not signed in)";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Connected: {connection.ConfigFolder ?? "unknown browser"} {who}, extension {connection.Version}, {Path.GetFileName(connection.Socket)}"));
        }

        return 0;
    }

    private async Task<int> HostAsync(CancellationToken cancellationToken)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var host = new ExtensionHost(stdin, stdout, ExtensionPlaces.SocketFor(socketFolder, Environment.ProcessId), LinuxChrome.Timeout, ParentProgram());
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The program of the process that started this one, from <c>/proc</c>; "" where that cannot be read.</summary>
    private static string ParentProgram()
    {
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.AsSpan().LastIndexOf(')') + 2)..].Split(' ');
            return new FileInfo($"/proc/{fields[1]}/exe").LinkTarget ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentException)
        {
            return "";
        }
    }
}
