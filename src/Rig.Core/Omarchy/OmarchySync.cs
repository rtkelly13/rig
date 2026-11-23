using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.Omarchy;

/// <summary>One planned or applied change to this machine's Omarchy settings.</summary>
/// <param name="Check">The setting and the two states being reconciled.</param>
/// <param name="Status"><c>planned</c>, <c>applied</c> or <c>error</c>.</param>
/// <param name="Detail">What the apply did, or why it could not.</param>
public sealed record OmarchyAction(OmarchyCheck Check, string Status, string Detail)
{
    /// <summary>Whether the change was written.</summary>
    public bool Applied => Status == "applied";

    /// <summary>Whether the change failed.</summary>
    public bool Error => Status == "error";
}

/// <summary><c>rig omarchy sync</c>: plan the differences, then write them with <c>--apply</c>.</summary>
public static class OmarchySync
{
    /// <summary>The status of a change a dry run only plans.</summary>
    public const string Planned = "planned";

    /// <summary>The status of a change the apply wrote.</summary>
    public const string AppliedStatus = "applied";

    /// <summary>The status of a change the apply could not write.</summary>
    public const string ErrorStatus = "error";

    /// <summary>The line that opens rig's block in <c>looknfeel.lua</c>.</summary>
    public const string HyprlandBlockBegin = "-- rig:omarchy:hyprland:begin (written by rig omarchy sync from config/omarchy.json; edit that instead)";

    /// <summary>The line that closes rig's block in <c>looknfeel.lua</c>.</summary>
    public const string HyprlandBlockEnd = "-- rig:omarchy:hyprland:end";

    /// <summary>Plans the differences, and with <paramref name="apply"/> writes them.</summary>
    /// <param name="policy">What the machine should look like.</param>
    /// <param name="state">What it looks like now.</param>
    /// <param name="paths">Where Omarchy keeps its files.</param>
    /// <param name="proc">Runs <c>omarchy</c>, <c>systemctl</c> and <c>sudo</c>.</param>
    /// <param name="apply">Whether to write. A dry run makes no changes and asks for no password.</param>
    /// <param name="cancellationToken">Stops a long <c>omarchy theme set</c>.</param>
    public static async Task<IReadOnlyList<OmarchyAction>> ApplyAsync(
        OmarchyPolicy policy,
        OmarchyState state,
        OmarchyPaths paths,
        IProc proc,
        bool apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(proc);
        var pending = OmarchyPlan.Compare(policy, state)
            .Where(check => check.Status == CheckStatus.Fail)
            .Select(check => new OmarchyAction(check, Planned, string.Empty))
            .ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        if (!state.Installed)
        {
            return [.. pending.Select(action => action with
            {
                Status = apply ? ErrorStatus : Planned,
                Detail = "Omarchy is not installed on this machine",
            })];
        }

        var shell = Indexes(pending, action => action.Check.Area is "idle" or "shell");
        var font = Indexes(pending, action => action.Check.Area == "font");
        var hyprland = Indexes(pending, action => action.Check.Area == "hyprland");
        var theme = Indexes(pending, action => action.Check.Area == "theme");
        var power = Indexes(pending, action => action.Check.Area == "power");
        var menu = Indexes(pending, action => action.Check.Area == "suspend" && action.Check.Setting == "menu");
        var logind = Indexes(pending, action => action.Check.Area == "logind" && action.Check.Setting != "lock-before-suspend");
        var unit = Indexes(pending, action => action.Check.Area == "logind" && action.Check.Setting == "lock-before-suspend");

        await SetAsync(pending, shell, apply, () => Task.FromResult(WriteShell(policy, paths))).ConfigureAwait(false);
        await SetAsync(pending, font, apply, () => SetTextSize(policy.Font!.BaseSize, proc, cancellationToken)).ConfigureAwait(false);
        await SetAsync(pending, hyprland, apply, () => Task.FromResult(WriteHyprland(policy.Hyprland!, paths))).ConfigureAwait(false);
        await SetAsync(pending, theme, apply, () => SetTheme(policy.Theme!, proc, cancellationToken)).ConfigureAwait(false);
        await SetAsync(pending, power, apply, () => SetPower(policy.Power!, proc, cancellationToken)).ConfigureAwait(false);
        await SetAsync(pending, menu, apply, () => Task.FromResult(SetSuspendMenu(policy.Suspend!, paths))).ConfigureAwait(false);
        await SetAsync(pending, logind, apply, () => WriteLogind(policy.Suspend!, state.LogindText, paths, proc, cancellationToken)).ConfigureAwait(false);
        await SetAsync(pending, unit, apply, () => SetSleepLock(policy.LockBeforeSuspend!.Value, proc, cancellationToken)).ConfigureAwait(false);
        return pending;
    }

    /// <summary>The logind drop-in's full text, including its section header.</summary>
    /// <param name="suspend">The suspend section of the policy.</param>
    public static string LogindFile(SuspendPolicy suspend)
    {
        ArgumentNullException.ThrowIfNull(suspend);
        return "[Login]\n" + OmarchyPlan.LogindContents(suspend);
    }

    /// <summary>
    /// <paramref name="text"/> with rig's spacing block replaced, or appended when it has none. Appending puts it after
    /// the user's own settings, and a later <c>hl.config</c> wins, so the policy holds.
    /// </summary>
    /// <param name="text">The current <c>looknfeel.lua</c>, or empty.</param>
    /// <param name="hyprland">The spacing to write.</param>
    /// <exception cref="InvalidDataException">The file has a begin marker without an end marker after it.</exception>
    public static string WithHyprlandBlock(string text, HyprlandPolicy hyprland)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hyprland);
        var block = string.Create(CultureInfo.InvariantCulture, $$"""
            {{HyprlandBlockBegin}}
            hl.config({
              general = {
                gaps_in = {{hyprland.GapsIn}},
                gaps_out = {{hyprland.GapsOut}},
                border_size = {{hyprland.BorderSize}},
              },
            })
            {{HyprlandBlockEnd}}
            """).ReplaceLineEndings("\n");
        var begin = text.IndexOf(HyprlandBlockBegin, StringComparison.Ordinal);
        if (begin < 0)
        {
            var body = text.TrimEnd('\n');
            return (body.Length == 0 ? string.Empty : body + "\n\n") + block + "\n";
        }

        var end = text.IndexOf(HyprlandBlockEnd, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"rig's block in looknfeel.lua has no closing {HyprlandBlockEnd} line");
        }

        return text[..begin] + block + text[(end + HyprlandBlockEnd.Length)..];
    }

    // Hyprland reloads its config when a sourced file is saved, so writing is enough.
    private static (bool Ok, string Detail) WriteHyprland(HyprlandPolicy hyprland, OmarchyPaths paths)
    {
        var path = paths.HyprLookAndFeelFile;
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        EnsureParent(path);
        File.WriteAllText(path, WithHyprlandBlock(current, hyprland));
        return (true, $"wrote rig's block in {path}");
    }

    private static async Task SetAsync(
        List<OmarchyAction> actions,
        List<int> indexes,
        bool apply,
        Func<Task<(bool Ok, string Detail)>> operation)
    {
        if (indexes.Count == 0 || !apply)
        {
            return;
        }

        try
        {
            var (ok, detail) = await operation().ConfigureAwait(false);
            foreach (var index in indexes)
            {
                actions[index] = actions[index] with { Status = ok ? AppliedStatus : ErrorStatus, Detail = detail };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            foreach (var index in indexes)
            {
                actions[index] = actions[index] with { Status = ErrorStatus, Detail = e.Message };
            }
        }
    }

    private static List<int> Indexes(List<OmarchyAction> actions, Func<OmarchyAction, bool> match)
    {
        List<int> indexes = [];
        for (var index = 0; index < actions.Count; index++)
        {
            if (match(actions[index]))
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    private static (bool Ok, string Detail) WriteShell(OmarchyPolicy policy, OmarchyPaths paths)
    {
        var desired = policy.Shell is null ? new JsonObject() : (JsonObject)policy.Shell.DeepClone();
        if (policy.Idle is { } idle)
        {
            desired["idle"] = JsonSerializer.SerializeToNode(
                new ShellIdleConfig { Screensaver = idle.ScreensaverSeconds, Lock = idle.LockSeconds },
                OmarchyConfigJsonContext.Default.ShellIdleConfig);
        }

        // Merge over the file as it is now, not as Read saw it, so a key another writer changed since is kept.
        var merged = ShellDocument.Merge(OmarchyState.ReadShell(paths.ShellFile), desired);
        EnsureParent(paths.ShellFile);
        File.WriteAllText(paths.ShellFile, JsonOutput.Format(merged, keepNonAscii: true) + "\n");
        return (true, $"wrote {paths.ShellFile}");
    }

    // Omarchy's own text-size command sets the shell's base size, GTK's scaling factor and the terminals'
    // font together; writing shell.toml alone would leave the other two at a different size.
    private static async Task<(bool Ok, string Detail)> SetTextSize(int size, IProc proc, CancellationToken cancellationToken)
    {
        var text = size.ToString(CultureInfo.InvariantCulture);
        var ran = await RunAsync(proc, cancellationToken, "omarchy", "display", "text", "size", text).ConfigureAwait(false);
        return ran.Ok ? (true, $"set the text size to {text}px") : (false, Failure("omarchy display text size", ran));
    }

    private static async Task<(bool Ok, string Detail)> SetTheme(string theme, IProc proc, CancellationToken cancellationToken)
    {
        var ran = await RunAsync(proc, cancellationToken, "omarchy", "theme", "set", theme).ConfigureAwait(false);
        return ran.Ok ? (true, $"applied {theme}") : (false, Failure("omarchy theme set", ran));
    }

    private static async Task<(bool Ok, string Detail)> SetPower(PowerPolicy power, IProc proc, CancellationToken cancellationToken)
    {
        foreach (var (source, profile) in new[] { ("ac", power.Ac), ("battery", power.Battery) })
        {
            var ran = await RunAsync(proc, cancellationToken, "omarchy", "powerprofiles", "set", source, profile).ConfigureAwait(false);
            if (!ran.Ok)
            {
                return (false, Failure($"omarchy powerprofiles set {source}", ran));
            }
        }

        var restore = await RunAsync(proc, cancellationToken, "omarchy", "powerprofiles", "set", "autodetect").ConfigureAwait(false);
        return restore.Ok ? (true, $"remembered {power.Ac} on AC and {power.Battery} on battery") : (false, Failure("omarchy powerprofiles set autodetect", restore));
    }

    private static (bool Ok, string Detail) SetSuspendMenu(SuspendPolicy suspend, OmarchyPaths paths)
    {
        var toggle = paths.ToggleFile("suspend-off");
        if (suspend.Menu == true)
        {
            if (File.Exists(toggle))
            {
                File.Delete(toggle);
            }

            return (true, "Suspend is available in Omarchy's system menu");
        }

        EnsureParent(toggle);
        File.WriteAllText(toggle, string.Empty);
        return (true, "Suspend is hidden from Omarchy's system menu");
    }

    private static async Task<(bool Ok, string Detail)> WriteLogind(
        SuspendPolicy suspend,
        string? previous,
        OmarchyPaths paths,
        IProc proc,
        CancellationToken cancellationToken)
    {
        var install = await InstallLogindAsync(LogindFile(suspend), paths, proc, cancellationToken).ConfigureAwait(false);
        if (!install.Ok)
        {
            return (false, $"writing {paths.LogindDropIn} needs sudo; run 'rig omarchy sync --apply' in a terminal and enter its password (exit {install.Code.ToString(CultureInfo.InvariantCulture)})");
        }

        var reload = await Sudo(proc, cancellationToken, "systemctl", "reload", "systemd-logind").ConfigureAwait(false);
        if (reload.Ok)
        {
            return (true, $"wrote {paths.LogindDropIn} and reloaded logind");
        }

        // The doctor compares the file, not logind's live values: leaving the new file in place would read
        // as clean while logind still runs the old settings. Put the old file back so the next sync retries.
        var restore = previous is null
            ? await Sudo(proc, cancellationToken, "rm", "-f", paths.LogindDropIn).ConfigureAwait(false)
            : await InstallLogindAsync(previous + "\n", paths, proc, cancellationToken).ConfigureAwait(false);
        var outcome = restore.Ok
            ? "restored the previous drop-in so the next sync retries"
            : $"could not restore the previous drop-in; remove {paths.LogindDropIn} by hand before the next sync";
        return (false, $"{Failure("sudo systemctl reload systemd-logind", reload)}; {outcome}");
    }

    private static async Task<Ran> InstallLogindAsync(string text, OmarchyPaths paths, IProc proc, CancellationToken cancellationToken)
    {
        var temporary = Path.Join(Path.GetTempPath(), $"rig-omarchy-{Guid.NewGuid():N}.conf");
        try
        {
            await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
            return await Sudo(proc, cancellationToken, "install", "-D", "-m", "0644", temporary, paths.LogindDropIn).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Task<Ran> Sudo(IProc proc, CancellationToken cancellationToken, params string[] argv) =>
        proc.RunAsync(new ProcRequest(["sudo", .. argv]) { ConsoleInput = true }, cancellationToken);

    private static async Task<(bool Ok, string Detail)> SetSleepLock(bool enabled, IProc proc, CancellationToken cancellationToken)
    {
        var verb = enabled ? "enable" : "disable";
        var ran = await RunAsync(proc, cancellationToken, "systemctl", "--user", verb, "--now", OmarchyState.SleepLockUnit).ConfigureAwait(false);
        return ran.Ok ? (true, $"{verb}d {OmarchyState.SleepLockUnit}") : (false, Failure($"systemctl --user {verb}", ran));
    }

    private static Task<Ran> RunAsync(IProc proc, CancellationToken cancellationToken, params string[] argv) =>
        proc.RunAsync(new ProcRequest(argv) { Timeout = TimeSpan.FromSeconds(120) }, cancellationToken);

    private static string Failure(string command, Ran ran) =>
        ran.Code == Ran.NotFound
            ? $"{command}: the program is not on PATH"
            : string.Create(CultureInfo.InvariantCulture, $"{command}: exit {ran.Code}: {FirstLine(ran)}");

    private static string FirstLine(Ran ran) =>
        (ran.Err.Trim() is { Length: > 0 } error ? error : ran.Out.Trim()).Split('\n')[0];

    private static void EnsureParent(string path) =>
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
}
