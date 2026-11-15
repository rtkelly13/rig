using System.Diagnostics;
using System.Text;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// Starting a plan entry as a process. Ported from the launch half of <c>desktop_manager.launching</c>.
/// </summary>
/// <param name="getVariable">Reads an environment variable, such as <c>LOCALAPPDATA</c>.</param>
/// <param name="home">What a leading <c>~</c> stands for.</param>
public sealed class Launching(Func<string, string?>? getVariable = null, string? home = null)
{
    /// <summary>Never terminated by cleanup, whatever the PID bookkeeping says.</summary>
    public static IReadOnlySet<string> ProtectedImageNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "warp.exe",
        "windowsterminal.exe",
        "wt.exe",
    };

    /// <summary>The Python <c>wt-pwsh</c> terminal shell.</summary>
    public const string WindowsTerminalShell = "wt-pwsh";

    private readonly Func<string, string?> _getVariable = getVariable ?? Environment.GetEnvironmentVariable;
    private readonly string _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The environment a launched window inherits: the agent-session variables are removed.</summary>
    public static IReadOnlyDictionary<string, string> CleanEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>())
        {
            if (entry.Key is not string name || entry.Value is not string value)
            {
                continue;
            }

            if (IsAgentSessionVariable(name))
            {
                continue;
            }

            environment[name] = value;
        }

        return environment;
    }

    /// <summary>Start <paramref name="entry"/>; the started process's id, when known.</summary>
    /// <exception cref="DesktopException">The entry's path or command is missing, or does not exist.</exception>
    public int? Launch(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions, string? restoreToken = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(substitutions);
        if (entry.Type == LaunchEntry.Terminal)
        {
            var command = BuildTerminalCommand(entry, substitutions, restoreToken);
            return Start(command, workingDirectory: null, detached: false);
        }

        if (entry.Type == "app")
        {
            var path = Placeholders.Substitute(entry.Path, substitutions);
            if (string.IsNullOrEmpty(path))
            {
                throw new DesktopException($"Launch entry `{entry.Name}` is missing a path.");
            }

            var expanded = ExpandPath(path);
            if (!File.Exists(expanded) && !Directory.Exists(expanded))
            {
                throw new DesktopException($"Launch entry `{entry.Name}` path does not exist: {expanded}");
            }

            var args = entry.Args
                .Select(arg => Placeholders.Substitute(arg, substitutions))
                .Where(arg => !string.IsNullOrEmpty(arg))
                .Select(arg => ExpandPath(arg!))
                .ToList();
            var cwd = Placeholders.Substitute(entry.Cwd, substitutions);
            if (!string.IsNullOrEmpty(cwd) && !Directory.Exists(ExpandPath(cwd)))
            {
                throw new DesktopException($"Launch entry `{entry.Name}` cwd does not exist: {cwd}");
            }

            return Start([expanded, .. args], string.IsNullOrEmpty(cwd) ? null : ExpandPath(cwd), detached: true);
        }

        var raw = Placeholders.Substitute(entry.Command, substitutions);
        if (string.IsNullOrEmpty(raw))
        {
            throw new DesktopException($"Launch entry `{entry.Name}` is missing a command.");
        }

        var directory = Placeholders.Substitute(entry.Cwd, substitutions);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(ExpandPath(directory)))
        {
            throw new DesktopException($"Launch entry `{entry.Name}` cwd does not exist: {directory}");
        }

        // A single string reaches CreateProcess verbatim; the list form would backslash-escape the command's own quotes.
        return StartText(raw, string.IsNullOrEmpty(directory) ? null : ExpandPath(directory));
    }

    /// <summary>The command line that opens <paramref name="entry"/>'s terminal.</summary>
    /// <exception cref="DesktopException">The terminal lacks a cwd or command.</exception>
    public IReadOnlyList<string> BuildTerminalCommand(
        LaunchEntry entry,
        IReadOnlyDictionary<string, string> substitutions,
        string? restoreToken = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(substitutions);
        var cwd = Placeholders.Substitute(entry.Cwd, substitutions);
        var command = Placeholders.Substitute(entry.Command, substitutions) ?? string.Empty;

        // A deferred entry restored at logon has its command withheld: it opens a bare shell that
        // `claude-sessions startup` fills.
        var bareShell = command.Length == 0 && entry.DefersCommandAtLogon && entry.Shell == WindowsTerminalShell;
        if (string.IsNullOrEmpty(cwd) || (command.Length == 0 && !bareShell))
        {
            throw new DesktopException($"Terminal entry `{entry.Name}` must define both cwd and command.");
        }

        if (!Directory.Exists(ExpandPath(cwd)))
        {
            throw new DesktopException($"Terminal entry `{entry.Name}` cwd does not exist: {cwd}");
        }

        if (command.Length > 0 && entry.Shell == WindowsTerminalShell && SelfUpdatingCommandName(command) is { } name)
        {
            command = PowershellSelfUpdatingLaunch(command, name);
        }

        if (restoreToken is not null)
        {
            command =
                $"$env:OPENCODE_RESTORE_TOKEN = {PowershellSingleQuote(restoreToken)}; "
                + $"$env:OPENCODE_RESTORE_ENTRY = {PowershellSingleQuote(entry.Name)}"
                + (command.Length > 0 ? $"; {command}" : string.Empty);
        }

        if (entry.Shell == WindowsTerminalShell)
        {
            var title = string.IsNullOrEmpty(entry.WindowTitle) ? [] : new[] { "--title", entry.WindowTitle };
            var commandArgs = command.Length > 0 ? new[] { "-NoExit", "-EncodedCommand", EncodedCommand(command) } : [];
            return
            [
                WindowsTerminalExecutable(),
                "-w",
                "new",
                "new-tab",
                .. title,
                "--startingDirectory",
                cwd,
                "pwsh.exe",
                "-NoLogo",
                .. commandArgs,
            ];
        }

        if (entry.Shell is "powershell" or "pwsh")
        {
            var shell = entry.Shell == "pwsh" ? "pwsh.exe" : "powershell.exe";
            var prefix = string.IsNullOrEmpty(entry.WindowTitle) ? string.Empty : $"$Host.UI.RawUI.WindowTitle = \"{entry.WindowTitle}\"; ";
            return [shell, "-NoExit", "-Command", $"{prefix}Set-Location -LiteralPath \"{cwd}\"; {command}"];
        }

        var titlePrefix = string.IsNullOrEmpty(entry.WindowTitle) ? string.Empty : $"title {entry.WindowTitle} && ";
        return ["cmd.exe", "/k", $"{titlePrefix}cd /d \"{cwd}\" && {command}"];
    }

    /// <summary>Where <c>wt.exe</c> is, or the bare name when it is not in the usual per-user location.</summary>
    public string WindowsTerminalExecutable()
    {
        var localAppData = _getVariable("LOCALAPPDATA");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(Path.Join(localAppData, "Microsoft", "WindowsApps", "wt.exe"));
        }

        candidates.Add(Path.Join(_home, "AppData", "Local", "Microsoft", "WindowsApps", "wt.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "wt.exe";
    }

    /// <summary>The first word of a terminal command, lowercased, without its folder or quotes.</summary>
    public static string? CommandName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var first = command.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim('"', '\'');
        var name = first[(first.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length > 0 ? name.ToLowerInvariant() : null;
    }

    private static bool IsAgentSessionVariable(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "NO_COLOR" or "CLAUDECODE" or "CLAUDE_PID" || upper.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal);
    }

    private static string? SelfUpdatingCommandName(string command) => CommandName(command) switch
    {
        "claude" or "claude.exe" => "claude",
        "opencode" => "opencode",
        "copilot" or "copilot.exe" => "copilot",
        _ => null,
    };

    private string ExpandPath(string path) =>
        _home.Length > 0
        && (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            ? _home + path[1..]
            : Environment.ExpandEnvironmentVariables(path);

    private static int? Start(IReadOnlyList<string> command, string? workingDirectory, bool detached)
    {
        ArgumentNullException.ThrowIfNull(command);
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            CreateNoWindow = detached,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in command.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new DesktopException($"`{command[0]}` did not start");
        using (process)
        {
            return process.Id;
        }
    }

    private static int? StartText(string command, string? workingDirectory)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
            Arguments = "/c " + command,
        };
        var process = Process.Start(start) ?? throw new DesktopException("The command did not start");
        using (process)
        {
            return process.Id;
        }
    }

    private static string EncodedCommand(string command) => Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

    private static string PowershellSingleQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Tools like Claude Code update themselves in the background: an already-running session reinstalls the npm
    // package, which deletes and rewrites the .exe the shim points at. A terminal launched during that window gets
    // "command not found" and drops to a bare prompt, so the check runs inside the terminal at launch time.
    private static string PowershellSelfUpdatingLaunch(string command, string commandName) =>
        $"$dmName = {PowershellSingleQuote(commandName)}; "
        + "function Test-DmReady { $c = Get-Command $dmName -CommandType Application,ExternalScript -ErrorAction SilentlyContinue "
        + "| Select-Object -First 1; if (-not $c) { return $false }; "
        + "if ($c.Source -match '\\.(cmd|ps1)$') { $m = [regex]::Match((Get-Content -Raw -LiteralPath $c.Source), "
        + "'(?:%dp0%|\\$basedir)[\\\\/]([^\"]+?\\.exe)'); "
        + "if ($m.Success) { return Test-Path -LiteralPath (Join-Path (Split-Path $c.Source) $m.Groups[1].Value) } }; return $true }; "
        + "for ($dmAttempt = 1; $dmAttempt -le 6; $dmAttempt++) { "
        + "$dmDeadline = (Get-Date).AddSeconds(90); $dmWaited = $false; "
        + "while (-not (Test-DmReady) -and (Get-Date) -lt $dmDeadline) { "
        + "if (-not $dmWaited) { Write-Host \"desktop-manager: '$dmName' is missing (likely mid-update); waiting...\" -ForegroundColor Yellow; "
        + "$dmWaited = $true }; Start-Sleep -Seconds 2 }; "
        + "$dmStarted = Get-Date; $global:LASTEXITCODE = 0; & { " + command + " }; $dmOk = $? -and ($LASTEXITCODE -eq 0); "
        + "if ($dmOk -or ((Get-Date) - $dmStarted).TotalSeconds -ge 20) { break }; "
        + "if ($dmAttempt -lt 6) { Write-Host \"desktop-manager: '$dmName' failed to start (attempt $dmAttempt/6); "
        + "retrying in 5s - Ctrl+C to stop\" -ForegroundColor Yellow; Start-Sleep -Seconds 5 } }";
}
