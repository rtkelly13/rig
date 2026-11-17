using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.ReviewQueue;
using Rig.Core.Tests;
using Rig.Web.TestSupport;

namespace Rig.Web.BrowserTests;

/// <summary>
/// The rig web host in this process, as the browser tests see it: on a free loopback port, serving this checkout's
/// frontend over a temp tree that holds the shared database, and, when asked, an estate to draw.
/// </summary>
/// <remarks>
/// The host reads nothing of this machine's. Its database, registry, assets and code root are all under one temp
/// folder, with <c>personal/</c> holding the estate's git checkouts.
/// </remarks>
public sealed class RigSite : IAsyncDisposable
{
    private readonly TempDir _dir;
    private readonly RigWebHost _host;

    private RigSite(TempDir dir, RigWebHost host)
    {
        _dir = dir;
        _host = host;
    }

    /// <summary>The host's address, without a trailing slash.</summary>
    public string Origin => _host.Address.GetLeftPart(UriPartial.Authority);

    /// <summary>The shared rig database the triage routes read.</summary>
    public string Db => Path.Join(_dir.Path, "rig.db");

    /// <summary>The triage snapshots and history in <see cref="Db"/>.</summary>
    public QueueStore Queue => new(Db);

    /// <summary>Starts a host over a fresh temp tree whose registry and checkouts hold <paramref name="estate"/>, if any.</summary>
    /// <param name="estate">Each project <c>owner/name</c> and its manifests (path to text); null leaves no registry.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<RigSite> StartAsync(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? estate, CancellationToken cancellationToken)
    {
        var dir = new TempDir();
        var home = dir.Folder("rig-checkout");
        var codeRoot = dir.Folder("code");
        _ = dir.Folder("code/personal");
        _ = dir.Folder("rig-checkout/assets");
        await File.WriteAllTextAsync(Path.Join(codeRoot, "AGENTS.md"), "# code root\n", cancellationToken);
        var registry = Path.Join(home, "config", "managed-projects.json");
        if (estate is not null)
        {
            await LayOutAsync(estate, registry, Path.Join(codeRoot, "personal"), cancellationToken);
        }

        var settings = new WebHostSettings(
            "127.0.0.1",
            0,
            IsPreview: false,
            Repo.Templates,
            Path.Join(home, "assets"),
            Path.Join(dir.Path, "rig.db"),
            registry,
            home,
            Path.Join(codeRoot, "personal"),
            Path.Join(dir.Path, "decisions.json"),
            Path.Join(dir.Path, "temp", "triage", "queue.md"),
            Path.Join(dir.Path, "temp", "traces", "sdk"),
            codeRoot);
        var host = await RigWebHost.CreateAsync(settings, cancellationToken: cancellationToken);
        var site = new RigSite(dir, host);
        try
        {
            await host.StartAsync(cancellationToken);
            await site.WarmAsync(cancellationToken);
        }
        catch
        {
            await site.DisposeAsync();
            throw;
        }

        return site;
    }

    /// <summary>The JSON the triage route answers with now.</summary>
    public async Task<string> TriageAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync(new Uri(Origin + "/api/triage"), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _dir.Dispose();
    }

    private static async Task LayOutAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> estate, string registry, string personal, CancellationToken cancellationToken)
    {
        var projects = new JsonArray([.. estate.Keys.Select(name => (JsonNode)new JsonObject
        {
            ["name"] = name,
            ["repo"] = $"https://github.com/owner/{name}",
            ["description"] = char.ToUpperInvariant(name[0]) + name[1..],
            ["domain"] = "Libraries",
        })]);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        await File.WriteAllTextAsync(registry, new JsonObject { ["projects"] = projects }.ToJsonString(), cancellationToken);
        var git = new Git(new SystemProc(), personal);
        foreach (var (name, files) in estate)
        {
            var folder = Path.Join(personal, name);
            _ = Directory.CreateDirectory(folder);
            foreach (var (relative, text) in files)
            {
                await File.WriteAllTextAsync(Path.Join(folder, relative), text, cancellationToken);
            }

            // Staged, not committed, so git lists the manifests.
            Check(await git.RunAsync(folder, ["init", "--quiet"], cancellationToken: cancellationToken), name);
            Check(await git.RunAsync(folder, ["add", "-f", "."], cancellationToken: cancellationToken), name);
        }
    }

    private static void Check(Ran ran, string name)
    {
        if (!ran.Ok)
        {
            throw new InvalidOperationException($"git failed laying out {name}: {ran.Err}");
        }
    }

    /// <summary>
    /// Asks the host for its own route and the page once, so the first page a test loads is not also its JIT warm-up:
    /// on a loaded runner a fresh host's first answers took longer than Playwright's five-second expectations.
    /// </summary>
    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        foreach (var path in new[] { "/api/status", "/" })
        {
            _ = await client.GetStringAsync(new Uri(Origin + path), cancellationToken);
        }
    }
}
