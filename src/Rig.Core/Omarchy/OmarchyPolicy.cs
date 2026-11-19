using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Omarchy;

/// <summary>Omarchy's idle cycle: when the screensaver starts and when the session locks, in seconds since idle began.</summary>
/// <param name="ScreensaverSeconds">Zero disables the screensaver.</param>
/// <param name="LockSeconds">Zero disables the idle lock.</param>
public sealed record IdlePolicy(int ScreensaverSeconds, int LockSeconds);

/// <summary>Sleep behaviour: the system menu entry and what logind does with the lid and the power keys.</summary>
/// <param name="Menu">Whether Suspend appears in Omarchy's system menu.</param>
/// <param name="Lid">What closing the lid does, one of <see cref="Actions"/>.</param>
/// <param name="PowerKey">What the power key does, one of <see cref="Actions"/>.</param>
/// <param name="SuspendKey">What the suspend key does, one of <see cref="Actions"/>.</param>
/// <param name="HibernateKey">What the hibernate key does, one of <see cref="Actions"/>.</param>
/// <param name="IdleAction">What logind does when its own idle timer expires, one of <see cref="Actions"/>.</param>
public sealed record SuspendPolicy(
    bool? Menu = null,
    string? Lid = null,
    string? PowerKey = null,
    string? SuspendKey = null,
    string? HibernateKey = null,
    string? IdleAction = null)
{
    /// <summary>The actions logind accepts for these keys.</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "ignore", "poweroff", "reboot", "halt", "suspend", "hibernate",
        "hybrid-sleep", "suspend-then-hibernate", "lock", "kexec", "soft-reboot",
    ];
}

/// <summary>The power profile Omarchy restores on AC and on battery.</summary>
/// <param name="Ac">The profile for external power, such as <c>performance</c>.</param>
/// <param name="Battery">The profile for battery power, such as <c>power-saver</c>.</param>
public sealed record PowerPolicy(string Ac, string Battery);

/// <summary>The shell's base font size, stored in <c>shell.toml</c>.</summary>
/// <param name="BaseSize">The size in points.</param>
public sealed record FontPolicy(int BaseSize);

/// <summary>
/// What <c>config/omarchy.json</c> declares for this machine. A section left out is not checked or written.
/// </summary>
/// <param name="Idle">The idle timers, or null to leave them alone.</param>
/// <param name="Suspend">Suspend and logind behaviour, or null to leave it alone.</param>
/// <param name="LockBeforeSuspend">Whether Omarchy's sleep-lock user unit should run, or null to leave it alone.</param>
/// <param name="Theme">The theme slug to apply, such as <c>tokyo-night</c>, or null to leave the theme alone.</param>
/// <param name="Power">The AC and battery power profiles, or null to leave them alone.</param>
/// <param name="Font">The shell's base font size, or null to leave it alone.</param>
/// <param name="Shell">Settings deep-merged into <c>shell.json</c>, such as <c>bar</c>, <c>dock</c> and <c>plugins</c>.</param>
public sealed record OmarchyPolicy(
    IdlePolicy? Idle,
    SuspendPolicy? Suspend,
    bool? LockBeforeSuspend,
    string? Theme,
    PowerPolicy? Power,
    FontPolicy? Font,
    JsonObject? Shell)
{
    /// <summary>The policy file's path under a rig checkout.</summary>
    public static readonly string RelativePath = Path.Join("config", "omarchy.json");

    /// <summary>A policy that declares nothing.</summary>
    public static readonly OmarchyPolicy Empty = new(null, null, null, null, null, null, null);

    /// <summary>The largest idle timeout Omarchy accepts: a day.</summary>
    public const int MaxIdleSeconds = 24 * 60 * 60;

    /// <summary>Reads the policy file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static OmarchyPolicy Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>Decodes a policy document.</summary>
    /// <param name="text">The JSON document.</param>
    /// <param name="source">The file or document the text came from, named in a rejection.</param>
    /// <exception cref="InvalidDataException">It is malformed: not JSON, a field of the wrong type, or an unknown action.</exception>
    public static OmarchyPolicy Parse(string text, string source = "omarchy policy")
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var node = JsonProblem.Parse(source, text);
            var root = node as JsonObject ?? throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(node));
            return Decode(root, source);
        }
        catch (JsonInputException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    private static OmarchyPolicy Decode(JsonObject root, string source) => new(
        root.ContainsKey("idle") ? DecodeIdle(RequiredObject(root, "idle")) : null,
        root.ContainsKey("suspend") ? DecodeSuspend(RequiredObject(root, "suspend")) : null,
        OptionalBool(root, "lock_before_suspend"),
        OptionalNonEmpty(OptionalString(root, "theme"), "theme", source),
        root.ContainsKey("power") ? DecodePower(RequiredObject(root, "power"), source) : null,
        root.ContainsKey("font") ? DecodeFont(RequiredObject(root, "font")) : null,
        DecodeShell(root));

    private static IdlePolicy DecodeIdle(JsonObject idle) =>
        new(RequiredInt(idle, "screensaver_seconds", 0, MaxIdleSeconds), RequiredInt(idle, "lock_seconds", 0, MaxIdleSeconds));

    private static SuspendPolicy DecodeSuspend(JsonObject suspend) => new(
        OptionalBool(suspend, "menu"),
        OptionalAction(suspend, "lid"),
        OptionalAction(suspend, "power_key"),
        OptionalAction(suspend, "suspend_key"),
        OptionalAction(suspend, "hibernate_key"),
        OptionalAction(suspend, "idle_action"));

    private static PowerPolicy DecodePower(JsonObject power, string source) =>
        new(RequiredNonEmpty(RequiredString(power, "ac"), "power.ac", source), RequiredNonEmpty(RequiredString(power, "battery"), "power.battery", source));

    private static FontPolicy DecodeFont(JsonObject font) => new(RequiredInt(font, "base_size", 6, 72));

    private static JsonObject? DecodeShell(JsonObject root)
    {
        if (JsonShape.Field(root, "shell") is not { } node)
        {
            return null;
        }

        var shell = node as JsonObject ?? throw JsonProblem.Expected(root, "shell", "an object", node);
        if (shell.ContainsKey("idle"))
        {
            throw JsonProblem.Expected(shell, "idle", "not declared under shell (use the top-level idle section)", shell["idle"]);
        }

        return (JsonObject)shell.DeepClone();
    }

    private static JsonObject RequiredObject(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) switch
        {
            JsonObject value => value,
            null => throw JsonProblem.Missing(parent, key, "an object"),
            var value => throw JsonProblem.Expected(parent, key, "an object", value),
        };

    private static string RequiredString(JsonObject parent, string key) =>
        OptionalString(parent, key) ?? throw JsonProblem.Missing(parent, key, "a string");

    private static string? OptionalString(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) switch
        {
            null => null,
            var value => JsonShape.AsText(value) ?? throw JsonProblem.Expected(parent, key, "a string", value),
        };

    private static bool? OptionalBool(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) switch
        {
            null => null,
            var value => JsonShape.AsBool(value) ?? throw JsonProblem.Expected(parent, key, "a boolean", value),
        };

    private static int RequiredInt(JsonObject parent, string key, int minimum, int maximum) =>
        JsonShape.Field(parent, key) switch
        {
            null => throw JsonProblem.Missing(parent, key, "a whole number"),
            var value => JsonShape.AsInt32(value) is { } number && number >= minimum && number <= maximum
                ? number
                : throw JsonProblem.Expected(parent, key, $"a whole number from {minimum} to {maximum}", value),
        };

    private static string? OptionalAction(JsonObject parent, string key) =>
        OptionalString(parent, key) is { } action && SuspendPolicy.Actions.Contains(action, StringComparer.Ordinal)
            ? action
            : JsonShape.Field(parent, key) is null
                ? null
                : throw JsonProblem.Expected(parent, key, $"one of {string.Join(", ", SuspendPolicy.Actions)}", parent[key]);

    private static string RequiredNonEmpty(string value, string path, string source) =>
        OptionalNonEmpty(value, path, source) ?? throw new InvalidDataException($"{source}: {path}: must not be blank");

    private static string? OptionalNonEmpty(string? value, string path, string source) =>
        value is null
            ? null
            : value.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : throw new InvalidDataException($"{source}: {path}: must not be blank");
}
