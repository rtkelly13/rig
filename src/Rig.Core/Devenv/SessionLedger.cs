using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Devenv;

/// <summary>Whether the session-ledger hook is wired into Claude Code's settings and its script is current.</summary>
public enum LedgerHookState
{
    /// <summary>Wired, and the installed script matches the bundled one.</summary>
    Current,

    /// <summary>No <c>SessionEnd</c> hook runs the ledger script.</summary>
    NotWired,

    /// <summary>Wired, but the script is not installed.</summary>
    ScriptMissing,

    /// <summary>Wired, but the installed script differs from the bundled one.</summary>
    ScriptOutdated,
}

/// <summary>
/// The session-ledger <c>SessionEnd</c> hook for Claude Code. <c>session-ledger.mjs</c> (embedded here) is copied to
/// <c>~/.claude/hooks/</c> and wired into <c>~/.claude/settings.json</c> under <c>hooks.SessionEnd</c>. On every
/// session end it writes <c>~/.claude/session-ends/&lt;session_id&gt;.json</c> (or <c>$CLAUDE_SESSION_LEDGER_DIR</c>)
/// with the reason the session ended.
/// </summary>
/// <remarks>
/// The workspace repo's devenv installs the same script to the same path and recognises an existing install by the
/// same rule: any <c>SessionEnd</c> hook whose script file is named <c>session-ledger.mjs</c>. Either installer can
/// run first and neither adds a second entry. The bundled script is kept byte-identical to the workspace copy.
/// </remarks>
public static class SessionLedger
{
    /// <summary>The script's file name, which is also how an existing install is recognised.</summary>
    public const string Marker = "session-ledger.mjs";

    /// <summary>
    /// The hook's timeout in seconds. SessionEnd hooks share a 1.5 s budget unless a hook asks for more; a cold Node
    /// start on Windows can take most of that.
    /// </summary>
    public const int TimeoutSeconds = 5;

    private static readonly HashSet<string> NodeExecutables = new(["node", "node.exe"], StringComparer.Ordinal);

    private static readonly Lazy<byte[]> Bundled = new(() =>
    {
        using var stream = typeof(SessionLedger).Assembly.GetManifestResourceStream("Rig.Core.Devenv.session-ledger.mjs")
            ?? throw new InvalidOperationException("session-ledger.mjs is not embedded in Rig.Core.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Canonical(copy.ToArray());
    });

    /// <summary>The bundled script, with LF line endings.</summary>
    public static ReadOnlySpan<byte> Script => Bundled.Value;

    /// <summary>Where the hook script is installed under <paramref name="home"/>.</summary>
    public static string HookPath(string home) => Path.Join(home, ".claude", "hooks", Marker);

    /// <summary>The hook command line: forward slashes, so Git Bash and PowerShell agree.</summary>
    public static string Command(string hookPath)
    {
        ArgumentNullException.ThrowIfNull(hookPath);
        return $"node \"{hookPath.Replace('\\', '/')}\"";
    }

    /// <summary>Whether the script at <paramref name="hookPath"/> matches the bundled one, line endings aside.</summary>
    public static bool ScriptCurrent(string hookPath)
    {
        try
        {
            return Canonical(File.ReadAllBytes(hookPath)).AsSpan().SequenceEqual(Script);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Copies the bundled script to <paramref name="hookPath"/>; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it cannot.</summary>
    public static void InstallScript(string hookPath)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hookPath))!);
        File.WriteAllBytes(hookPath, Bundled.Value);
    }

    /// <summary>
    /// Whether <paramref name="command"/> executes the ledger script, from any folder: <c>node [flags] &lt;path&gt;</c>
    /// or the script path alone, quoted or not, with Windows or POSIX separators. A command that only passes the file
    /// name as an argument (<c>echo session-ledger.mjs</c>) does not count.
    /// </summary>
    public static bool RunsLedger(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        IReadOnlyList<string> tokens;
        try
        {
            tokens = Shlex.Split(command.Replace('\\', '/'));
        }
        catch (FormatException)
        {
            return false;
        }

        if (tokens.Count == 0)
        {
            return false;
        }

        var script = NodeExecutables.Contains(BaseName(tokens[0]).ToLowerInvariant())
            ? tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-')) ?? string.Empty
            : tokens[0];
        return BaseName(script) == Marker;
    }

    /// <summary>
    /// Adds the ledger hook to <paramref name="settings"/> in place unless one is already wired, and returns the change
    /// lines. A <c>hooks</c> value that isn't an object, or a <c>SessionEnd</c> value that isn't a list, is the user's
    /// own shape: it is left alone rather than overwritten.
    /// </summary>
    public static IReadOnlyList<string> Merge(JsonObject settings, string command)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var hooks = settings.ContainsKey("hooks") ? settings["hooks"] as JsonObject : [];
        var groups = hooks is null ? null : hooks.ContainsKey("SessionEnd") ? hooks["SessionEnd"] as JsonArray : [];
        if (hooks is null || groups is null || HasHook(groups))
        {
            return [];
        }

        hooks["SessionEnd"] ??= groups;
        settings["hooks"] ??= hooks;
        groups.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = TimeoutSeconds }),
        });
        return [$"+ hooks.SessionEnd: {command}"];
    }

    /// <summary>Whether the hook is wired into <paramref name="settings"/> and the script at <paramref name="hookPath"/> is current.</summary>
    public static LedgerHookState State(JsonObject settings, string hookPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var groups = settings["hooks"] is JsonObject hooks && hooks["SessionEnd"] is JsonArray found ? found : null;
        if (groups is null || !HasHook(groups))
        {
            return LedgerHookState.NotWired;
        }

        return !File.Exists(hookPath) ? LedgerHookState.ScriptMissing
            : !ScriptCurrent(hookPath) ? LedgerHookState.ScriptOutdated
            : LedgerHookState.Current;
    }

    /// <summary>How <c>devenv doctor</c> and <c>devenv claude --check</c> describe <paramref name="state"/>.</summary>
    public static string Describe(this LedgerHookState state) => state switch
    {
        LedgerHookState.Current => "installed",
        LedgerHookState.NotWired => "not wired in settings.json",
        LedgerHookState.ScriptMissing => "script missing",
        _ => "script out of date",
    };

    /// <summary>Whether any hook group in <paramref name="groups"/> already runs the session ledger.</summary>
    public static bool HasHook(JsonArray groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return groups
            .OfType<JsonObject>()
            .Select(group => group["hooks"])
            .OfType<JsonArray>()
            .SelectMany(hooks => hooks.OfType<JsonObject>())
            .Select(hook => hook["command"])
            .Any(command => command is JsonValue value && value.GetValueKind() == JsonValueKind.String && RunsLedger(value.GetValue<string>()));
    }

    private static string BaseName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static byte[] Canonical(byte[] data)
    {
        var text = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
            {
                continue;
            }

            text.Add(data[i]);
        }

        return [.. text];
    }
}
