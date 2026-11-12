using System.Diagnostics.CodeAnalysis;
using System.Net;
using Rig.Cli.Agent;
using Rig.Cli.Browser;
using Rig.Cli.Chrome;
using Rig.Cli.Devenv;
using Rig.Cli.Estate;
using Rig.Cli.Flags;
using Rig.Cli.Forwarding;
using Rig.Cli.Governance;
using Rig.Cli.Jobs;
using Rig.Cli.Latex;
using Rig.Cli.Mail;
using Rig.Cli.Models;
using Rig.Cli.Otel;
using Rig.Cli.Policy;
using Rig.Cli.Skills;
using Rig.Cli.Status;
using Rig.Cli.Vault;
using Rig.Cli.Web;
using Rig.Cli.Workspace;
using Rig.Core.Browser;
using Rig.Core.Devenv;
using Rig.Core.Estate;
using Rig.Core.Flags;
using Rig.Core.GitHub;
using Rig.Core.Jobs;
using Rig.Core.Orca;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Services;
using Rig.Core.Skills;
using Rig.Core.Vault;
using Rig.Core.Workspace;
using Rig.Web;

namespace Rig.Cli;

/// <summary>The composition root: real processes, the real console and this machine's rig checkout.</summary>
[ExcludeFromCodeCoverage(Justification = "Wiring only; RigApp and PythonRig carry the behaviour and are tested.")]
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var codeRoot = CodeRoot.Current();
        // RIG_HOME first; then the runtime copy a published rigw or rig runs from, so its jobs use that copy; then the checkout.
        var rigHome = Environment.GetEnvironmentVariable(PythonRig.HomeVariable);
        var checkout = PythonRig.ResolveCheckout(
            string.IsNullOrEmpty(rigHome) ? JobsSite.CopyOf(AppContext.BaseDirectory, AppData.Current()) : rigHome,
            codeRoot);
        var proc = new SystemProc();
        var git = new Git(proc, codeRoot);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var macOs = OperatingSystem.IsMacOS();
        // The package registries (npm, NuGet), read by `workspace packages`. NuGet's registration resource is served gzipped.
        using var registryHandler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, CheckCertificateRevocationList = true };
        using var registries = new HttpClient(registryHandler) { Timeout = TimeSpan.FromSeconds(30) };
        var workspace = new WorkspaceCommand(
            new WorkspaceHost(
                Path.Join(codeRoot, "personal"),
                Path.Join(checkout, "config", EstateScopes.FileName),
                home,
                ManagedProjects.RegistryFile(checkout, Environment.CurrentDirectory),
                AgentHost.ConfigUnder(Environment.GetEnvironmentVariable("WORKSPACE_HOME"), home)),
            proc,
            TimeProvider.System,
            Console.Out,
            registries);
        // Created per command; HttpClient opens no connection until the health probe asks for one. Redirects are not
        // followed, so only a 200 from the health endpoint itself counts as healthy.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        using var http = new HttpClient(handler);
        // otel-stack's clients, each with the Python's timeout: downloads 120 s to the headers, ClickHouse and the sender 30 s.
        // Obsidian plugin downloads share the downloads client.
        using var downloads = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var clickhouseHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var otlp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // The Vercel API, read by `repo-governance vercel-gating` and `vercel-projects`; the token is resolved on first use.
        using var vercelHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var vercel = new Rig.Core.Governance.VercelApi(vercelHttp, new Rig.Core.Governance.VercelToken(Environment.GetEnvironmentVariable, home, proc, TimeProvider.System).ResolveAsync);
        var otelPaths = new OtelPaths(AppData.Current());
        var detached = OperatingSystem.IsWindows() ? (IDetachedStart)new WindowsDetachedStart() : new UnixDetachedStart(proc);
        var signals = new ProcessSignals();
        var probe = new TcpPortProbe();
        var otelRuntime = new CollectorRuntime(otelPaths, proc, detached, OperatingSystem.IsWindows());
        var devenvHost = new DevenvHost(
            home,
            Path.Join(checkout, "config", "cli-allowlists.json"),
            OperatingSystem.IsWindows(),
            checkout,
            codeRoot,
            Environment.GetEnvironmentVariable("SHELL"),
            ProgramPath.Find,
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            OperatingSystem.IsMacOS(),
            Environment.CurrentDirectory,
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Console.In);
        var python = new PythonRig(proc, checkout);
        var app = new RigApp(
            python,
            new GhApi(proc),
            async _ => RepoSlug.FromRemote(await git.OutAsync(null, "remote", "get-url", "origin").ConfigureAwait(false)),
            Console.Out,
            Console.Error,
            [
                new LatexCommand(
                    proc,
                    new LatexHost(ProgramPath.Find, Environment.GetEnvironmentVariable("PATH") ?? string.Empty, codeRoot, Environment.CurrentDirectory),
                    Console.Out,
                    Console.Error).Build,
                new MdLintCommand(
                    proc,
                    new MdLintHost(checkout, Environment.GetEnvironmentVariable, home),
                    Console.Error).Build,
                new VaultCommand(
                    proc,
                    new VaultHost(Environment.GetEnvironmentVariable, home),
                    new KnowledgeServerCommand(
                        proc,
                        async ct => new KnowledgeServer(
                            proc,
                            new Launchd(proc, Launchd.AgentsDirUnder(home), Launchd.GuiDomain(macOs ? await KnowledgeServerCommand.UserIdAsync(proc, ct).ConfigureAwait(false) : 0)),
                            new KnowledgeServerPlaces(home, AppData.Current()),
                            ProgramPath.Find,
                            http),
                        macOs,
                        ProgramPath.Find,
                        Console.Out,
                        Console.Error),
                    Console.Out,
                    Console.Error).Build,
                new ChromeTabsCommand(
                    Environment.GetEnvironmentVariable,
                    home,
                    TimeProvider.System,
                    TimeZoneInfo.Local,
                    Console.Out,
                    OperatingSystem.IsWindowsVersionAtLeast(8, 1) ? new ChromeTabsDriver(
                            WindowsChrome.Open,
                            new GhApi(proc),
                            () => ChromeTabsDriver.LoadOwners(ManagedProjects.RegistryFile(checkout, Environment.CurrentDirectory)))
                        : null).Build,
                new FlagsCommand(new FlagStore(Path.Join(AppData.Current(), "rig.db")), Console.Out).Build,
                new JobsCommand(JobsHostFor(codeRoot, checkout, home, proc), Console.Out).Build,
                new MailCommand(
                    new MailHost(Environment.GetEnvironmentVariable, home, Path.Join(codeRoot, "personal"), TimeProvider.System),
                    Console.Out,
                    Console.Error).Build,
                new OtelStackCommand(
                    new OtelHost(
                        otelPaths,
                        OtelPins.PlatformKey,
                        macOs,
                        Path.Join(checkout, "temp", "traces", "sdk"),
                        Environment.GetEnvironmentVariable,
                        TimeProvider.System,
                        TimeZoneInfo.Local),
                    new OtelParts(
                        otelRuntime,
                        probe,
                        new OtelInstaller(downloads),
                        async ct => new Launchd(proc, Launchd.AgentsDirUnder(home), Launchd.GuiDomain(await KnowledgeServerCommand.UserIdAsync(proc, ct).ConfigureAwait(false))),
                        new ClickHousePair(otelPaths, signals, detached, probe, otelRuntime, TimeProvider.System, Task.Delay),
                        new ClickHouseClient(clickhouseHttp),
                        new OtlpSender(otlp, new Uri($"http://{ClickHouseConfig.Host}:{ClickHouseConfig.CollectorPort}")),
                        Task.Delay),
                    Console.Out,
                    Console.Error).Build,
                new StatusCommand(proc, git, Environment.CurrentDirectory, Console.Out).Build,
                () => new ModelsCommand(new ModelDiscovery(proc, home, ProgramPath.Find), Console.Out).Build(),
                new PolicyCommand(
                    new PolicyHost(Path.Join(checkout, "config", "cli-allowlists.json"), home, Path.Join(codeRoot, "personal"), OperatingSystem.IsWindows()),
                    Console.Out,
                    Console.Error).Build,
                new SkillsCommand(
                    new SkillsHost(checkout, home, Environment.CurrentDirectory, Path.Join(codeRoot, "external"), OperatingSystem.IsWindows()),
                    git,
                    new SystemDirectoryLinker(),
                    Console.Out,
                    Console.Error).Build,
                new BrowserCommand(
                    new BrowserHost(
                        home,
                        Environment.GetEnvironmentVariable("SHELL"),
                        OperatingSystem.IsWindows(),
                        Environment.GetEnvironmentVariable,
                        places => OperatingSystem.IsWindows() ? new WindowsUserEnvironment() : new ShellProfileEnvironment(places.ShellProfile)),
                    proc,
                    Console.Out,
                    Console.Error).Build,
                new DevenvCommand(
                    devenvHost,
                    new ObsidianPlugins(downloads),
                    new NerdFonts(downloads),
                    proc,
                    Console.Out,
                    Console.Error).Build,
                new DoctorCommand(devenvHost, proc, TimeProvider.System, Console.Out).Build,
                workspace.Build,
                workspace.BuildSync,
                new EstateCommand(
                    new EstateHost(
                        codeRoot,
                        home,
                        Environment.CurrentDirectory,
                        Path.Join(checkout, "config", EstateScopes.FileName),
                        AgentHost.ConfigUnder(Environment.GetEnvironmentVariable("WORKSPACE_HOME"), home),
                        Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                        ManagedProjects.RegistryFile(checkout, Environment.CurrentDirectory)),
                    OrcaHost.Current,
                    proc,
                    new GhApi(proc),
                    TimeProvider.System,
                    Caches.FindTool,
                    Console.Out,
                    Console.Error,
                    new WrapupHost(
                        WrapupAgentRig(checkout),
                        new WrapupFollower(AppData.Current(), detached, SelfArgv()),
                        new SystemWrapupClock(TimeProvider.System),
                        WrapupStamps.NewAskId)).Build,
                new GovernanceCommand(new GovernanceHost(codeRoot, Environment.CurrentDirectory, checkout), proc, TimeProvider.System, Console.Out, vercel: vercel).Build,
                new WebCommand(
                    new WebPlaces(
                        checkout,
                        ManagedProjects.RegistryFile(checkout, Environment.CurrentDirectory),
                        Environment.CurrentDirectory,
                        Path.Join(AppData.Current(), "rig.db"),
                        WebPlaces.PythonRoutesFrom(Environment.GetEnvironmentVariable(WebPlaces.PythonRoutesVariable))),
                    args => ProcessChildServer.Start(python.Argv(args)),
                    probe,
                    OpenBrowser,
                    Task.Delay,
                    Console.Out,
                    Console.Error).Build,
            ],
            new AgentLauncher(
                proc,
                new AgentHost(AgentHost.ConfigUnder(Environment.GetEnvironmentVariable("WORKSPACE_HOME"), home), Environment.CurrentDirectory, ProgramPath.Find),
                Console.Out,
                Console.Error));
        if (app.HandsOver(args))
        {
            // The Python CLI or the agent shares this console and handles Ctrl+C itself; rig waits for it and returns its code.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
        }

        return await app.RunAsync(args, CancellationToken.None).ConfigureAwait(false);
    }

    // The rig the asked agent runs `report` with: the checkout's own, since the agent's shell is another repo's worktree.
    private static string WrapupAgentRig(string checkout)
    {
        var found = OperatingSystem.IsWindows() ? Path.Join(checkout, ".venv", "Scripts", "rig.exe") : Path.Join(checkout, ".venv", "bin", "rig");
        return File.Exists(found) ? found : "rig";
    }

    // The default browser, as Python's webbrowser.open: the shell opens the URL.
    private static void OpenBrowser(Uri page) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    // How to start this same rig again, for the background wrap-up follower.
    private static string[] SelfArgv()
    {
        var process = Environment.ProcessPath ?? "rig";
        var entry = typeof(Program).Assembly.Location;
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entry.Length > 0 ? [process, entry] : [process];
    }

    // The registry as the checkout's `desktop-manager jobs` built it: this checkout's scripts and the per-user plan.
    private static JobsHost JobsHostFor(string codeRoot, string checkout, string home, IProc proc)
    {
        var windows = OperatingSystem.IsWindows();
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var machineConfig = AgentHost.ConfigUnder(Environment.GetEnvironmentVariable("WORKSPACE_HOME"), home);
        var site = JobsSite.Locate(
            checkout,
            AppData.Current(),
            JobRegistry.DefaultPlanPath(Environment.GetEnvironmentVariable("APPDATA"), home, windows),
            Path.Join(codeRoot, "personal"));
        var environment = new JobsEnvironment(
            Path.Join(checkout, ".venv", windows ? "Scripts" : "bin"),
            site.Plan,
            MachineConfig.WorkspaceRepo(machineConfig, Path.GetDirectoryName(site.PersonalRoot) ?? codeRoot, home),
            site.PersonalRoot,
            MachineConfig.WorkDirs(machineConfig, codeRoot, home),
            Decisions.DefaultFeedPath(localAppData, home),
            OrcaLocations.Current.EstateUserData(),
            JobRegistry.ChromeTabsLog(localAppData, home),
            windows);
        return new JobsHost(
            JobState.JobsDir(AppData.Current()),
            () => JobRegistry.Default(environment),
            () => new FlagStore(Path.Join(AppData.Current(), "rig.db")).Effective(),
            TimeProvider.System,
            now => BootTime.At(now, TimeSpan.FromMilliseconds(Environment.TickCount64), windows),
            windows ? new Schtasks(proc) : null,
            new ProcessJobExecutor(TimeProvider.System, site.Environment),
            () => OperatingSystem.IsWindowsVersionAtLeast(6, 1) ? Rig.Core.Chrome.Presence.IdleSeconds(new Rig.Core.Chrome.Windows.NativePresence()) : double.PositiveInfinity,
            (path, wait) => RunLock.Hold(path, wait, Thread.Sleep),
            () => new TraceFileWriter(
                "desktop-manager-jobs",
                new Dictionary<string, string> { [JobRunner.Source.Name] = JobRunner.Scope },
                name => Environment.GetEnvironmentVariable(name) ?? site.Environment.GetValueOrDefault(name),
                Path.Join(checkout, "temp", "traces", "sdk"),
                TimeProvider.System),
            ShellWindow.WaitFor,
            Path.Join(AppData.Current(), "traces"));
    }
}
