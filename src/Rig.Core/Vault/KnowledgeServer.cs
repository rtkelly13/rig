using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Unicode;
using Rig.Core.Processes;
using Rig.Core.Services;

namespace Rig.Core.Vault;

/// <summary>How Claude Code's <c>~/.claude.json</c> registers the server at user scope: absent, or its transport and target.</summary>
/// <param name="Transport">The entry's <c>type</c>; <c>stdio</c> when the entry leaves it out, null when absent or not a string.</param>
/// <param name="Target">The entry's <c>url</c>, or its <c>command</c> when the url is missing or empty.</param>
public sealed record Registration(string? Transport, string? Target)
{
    /// <summary>No entry.</summary>
    public static Registration Absent { get; } = new(null, null);

    /// <summary>Whether the entry already points at the shared server.</summary>
    public bool Shared => Transport == "http" && Target == KnowledgeServer.McpUrl();
}

/// <summary>Where the knowledge server's files live for one user.</summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="AppData">rig's app-state folder; see <see cref="Roots.AppData"/>.</param>
public sealed record KnowledgeServerPlaces(string Home, string AppData)
{
    /// <summary>The vault repository, which holds <c>.ok/</c> and the skill library; the notes are under it in <c>Vault/</c>.</summary>
    public string ProjectRoot => Path.Join(Home, "code", "personal", "obsidian-vault");

    /// <summary>Where the pinned package is installed.</summary>
    public string Prefix => Path.Join(AppData, "open-knowledge");

    /// <summary>The server's log, standard output and error both.</summary>
    public string LogPath => Path.Join(AppData, "logs", "open-knowledge.log");

    /// <summary>Claude Code's user settings, read and never written here.</summary>
    public string ClaudeJson => Path.Join(Home, ".claude.json");
}

/// <summary>
/// One OpenKnowledge server for the vault, shared by every Claude session, ported from <c>rig.obsidian.knowledge_server</c>.
/// Registered as a stdio server, OpenKnowledge starts <c>npx … ok mcp</c> in every session, about 120 MB each; one resident
/// <c>ok start</c> on a fixed port serves the same tools at <c>/mcp</c> to any number of sessions, and its editor at <c>/</c>.
/// The server is rooted at the vault and answers only for it. It runs as a macOS LaunchAgent; the platform check is the
/// command line's, so everything here runs, and is tested, on any OS.
/// </summary>
/// <param name="proc">Runs <c>npm</c>.</param>
/// <param name="launchd">Loads and queries the LaunchAgent.</param>
/// <param name="places">Where the files live.</param>
/// <param name="which">Finds a program on PATH, or null; <see cref="ProgramPath.Find(string)"/> outside tests.</param>
/// <param name="http">Sends the health probe; it must not follow redirects, as httpx did not.</param>
public sealed class KnowledgeServer(IProc proc, Launchd launchd, KnowledgeServerPlaces places, Func<string, string?> which, HttpMessageInvoker http)
{
    /// <summary>The LaunchAgent's label.</summary>
    public const string Label = "dev.rtkelly.rig.open-knowledge";

    /// <summary>The npm package.</summary>
    public const string Package = "@inkeep/open-knowledge";

    /// <summary>Pinned so the resident server changes only through a PR; <c>npx …@latest</c> moved on every session.</summary>
    public const string Version = "0.89.4";

    /// <summary>The port the server listens on, on 127.0.0.1.</summary>
    public const int Port = 47311;

    /// <summary>The name Claude Code knows the server by; the same one <c>ok init</c> registers.</summary>
    public const string ServerName = "open-knowledge";

    /// <summary>Seconds launchd waits before restarting a server that exited.</summary>
    public const int ThrottleSeconds = 60;

    /// <summary>How long npm gets to install the package.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(600);

    /// <summary>How long the health probe waits.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(2);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>The MCP endpoint Claude connects to.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055", Justification = "Compared and passed to claude as text, exactly as written.")]
    public static string McpUrl(int port = Port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/mcp";

    /// <summary>The editor, for reviewing Markdown in the browser.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055", Justification = "Compared and passed to claude as text, exactly as written.")]
    public static string EditorUrl(int port = Port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>Where the pinned package is installed.</summary>
    public KnowledgeServerPlaces Places => places;

    /// <summary>The LaunchAgent's plist.</summary>
    public string PlistPath => Path.Join(launchd.AgentsDir, $"{Label}.plist");

    // npm's folder for the scoped package, with this platform's separators throughout, as pathlib gives it.
    private static string PackageDir(string prefix) =>
        Path.Join([prefix, "node_modules", .. Package.Split('/')]);

    /// <summary>The package's CLI module, run with node directly: launchd has no shell PATH for the <c>ok</c> shebang.</summary>
    public static string EntryPoint(string prefix) => Path.Join(PackageDir(prefix), "dist", "cli.mjs");

    /// <summary>The installed package's version, or null when it is missing or its manifest unreadable.</summary>
    public static string? InstalledVersion(string prefix)
    {
        byte[] manifest;
        try
        {
            manifest = File.ReadAllBytes(Path.Join(PackageDir(prefix), "package.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Text(Field(Parse(manifest), "version"));
    }

    /// <summary>Installs the pinned package under <paramref name="prefix"/> (rig's data folder by default), unless that version is already there.</summary>
    /// <returns>The package's entry point.</returns>
    /// <exception cref="FileNotFoundException">npm is not on PATH.</exception>
    /// <exception cref="ProcException">npm failed.</exception>
    public async Task<string> InstallAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        var target = prefix ?? places.Prefix;
        if (InstalledVersion(target) != Version)
        {
            var npm = which("npm") ?? throw new FileNotFoundException("npm is not on PATH; install Node.js 24 or later");
            Directory.CreateDirectory(target);
            string[] argv = [npm, "install", "--prefix", target, "--no-fund", "--no-audit", $"{Package}@{Version}"];
            (await proc.RunAsync(new ProcRequest(argv) { Timeout = InstallTimeout }, cancellationToken).ConfigureAwait(false)).EnsureOk(argv);
        }

        return EntryPoint(target);
    }

    /// <summary>
    /// The server's command line. <c>--idle-shutdown off</c> matters: with <c>KeepAlive</c> the default 30 minutes would make
    /// launchd restart it every half hour.
    /// </summary>
    public static IReadOnlyList<string> ProgramArguments(string node, string script, string root, int port = Port) =>
        [node, script, "--cwd", root, "start", "--port", port.ToString(CultureInfo.InvariantCulture), "--idle-shutdown", "off", "--no-open-browser"];

    /// <summary>The LaunchAgent's plist.</summary>
    public byte[] Render(string node, string script, string root, int port = Port) =>
        Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = Label,
            ["ProgramArguments"] = ProgramArguments(node, script, root, port),
            ["WorkingDirectory"] = root,
            ["RunAtLoad"] = true,
            ["KeepAlive"] = true,
            ["ThrottleInterval"] = ThrottleSeconds,
            ["ProcessType"] = "Background",
            ["LowPriorityIO"] = true,
            ["Nice"] = 10,
            ["StandardOutPath"] = places.LogPath,
            ["StandardErrorPath"] = places.LogPath,
        });

    /// <summary>node on PATH.</summary>
    /// <exception cref="FileNotFoundException">node is not on PATH.</exception>
    public string NodePath() => which("node") ?? throw new FileNotFoundException("node is not on PATH; install Node.js 24 or later");

    /// <summary>Writes and loads the LaunchAgent that runs <paramref name="script"/> rooted at <paramref name="root"/> (the vault by default).</summary>
    /// <returns>The plist's path.</returns>
    public async Task<string> InstallServiceAsync(string script, string? root = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(places.LogPath)!);
        await launchd.LoadAsync(PlistPath, Render(NodePath(), script, root ?? places.ProjectRoot), cancellationToken).ConfigureAwait(false);
        return PlistPath;
    }

    /// <summary>Unloads and deletes the LaunchAgent; false when it was not installed.</summary>
    public Task<bool> UninstallServiceAsync(CancellationToken cancellationToken = default) => launchd.RemoveAsync(PlistPath, cancellationToken);

    /// <summary>Whether the LaunchAgent's plist is in place.</summary>
    public bool ServiceInstalled => File.Exists(PlistPath);

    /// <summary>Whether launchd has the LaunchAgent loaded.</summary>
    public Task<bool> ServiceLoadedAsync(CancellationToken cancellationToken = default) => launchd.LoadedAsync(Label, cancellationToken);

    /// <summary>Restarts the server, for example after the vault's <c>.ok/config.yml</c> changes.</summary>
    public Task RestartAsync(CancellationToken cancellationToken = default) => launchd.KickstartAsync(Label, restart: true, cancellationToken);

    /// <summary>Whether the server answers its health probe on <paramref name="port"/> with a 200 within <paramref name="timeout"/>.</summary>
    public async Task<bool> HealthyAsync(int port = Port, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? HealthTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{EditorUrl(port)}healthz"));
        try
        {
            using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// The user-scope entry in the text of <c>~/.claude.json</c>. A file that is not a JSON object, or a value of the wrong
    /// type, reads as absent, as pydantic's lenient decode did; where a key repeats, the last one counts.
    /// </summary>
    public static Registration ReadRegistration(byte[] text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Field(Field(Parse(text), "mcpServers"), ServerName) is not { ValueKind: JsonValueKind.Object } entry)
        {
            return Registration.Absent;
        }

        var type = Field(entry, "type") is { } given ? Text(given) : "stdio";
        var url = Text(Field(entry, "url"));
        return new Registration(type, string.IsNullOrEmpty(url) ? Text(Field(entry, "command")) : url);
    }

    /// <summary>The registration in this user's <c>~/.claude.json</c>; absent when it cannot be read.</summary>
    public Registration CurrentRegistration()
    {
        try
        {
            return ReadRegistration(File.ReadAllBytes(places.ClaudeJson));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Registration.Absent;
        }
    }

    /// <summary>
    /// The <c>claude mcp</c> commands that point the user-scope entry at the shared server; none when it already does.
    /// <c>~/.claude.json</c> is rewritten by every running session, so rig goes through <c>claude mcp</c>, which owns it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ConnectCommands(Registration have, string claude = "claude")
    {
        ArgumentNullException.ThrowIfNull(have);
        if (have.Shared)
        {
            return [];
        }

        string[] add = [claude, "mcp", "add", "--scope", "user", "--transport", "http", ServerName, McpUrl()];
        return have.Transport is null ? [add] : [[claude, "mcp", "remove", "--scope", "user", ServerName], add];
    }

    // The JSON document, or null when it is not UTF-8 JSON. pydantic refused a byte-order mark and invalid UTF-8 anywhere.
    private static JsonElement? Parse(byte[] text)
    {
        if (text.AsSpan().StartsWith(Bom) || !Utf8.IsValid(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The last value under name when element is an object, else null.
    private static JsonElement? Field(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } found)
        {
            return null;
        }

        JsonElement? value = null;
        foreach (var property in found.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                value = property.Value;
            }
        }

        return value;
    }

    // A string value, or null for anything else, including a string with an unpaired surrogate escape.
    private static string? Text(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.String } value)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
