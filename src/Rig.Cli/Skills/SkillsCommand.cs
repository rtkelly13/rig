using System.CommandLine;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Processes;
using Rig.Core.Skills;
using Rig.Core.Vault;

namespace Rig.Cli.Skills;

/// <summary>Where <c>rig skills</c> reads and writes.</summary>
/// <param name="Checkout">The rig checkout holding the canonical catalog and its allowlists.</param>
/// <param name="Home">The home folder holding each system's skills folder, unless <c>--home</c> names another.</param>
/// <param name="WorkingDirectory">Where a repository's own <c>skills/</c> is looked for.</param>
/// <param name="ExternalClones">Where vendor repositories are cloned: the code root's untrusted <c>external/</c>.</param>
/// <param name="Windows">Whether names match and paths compare ignoring case.</param>
internal sealed record SkillsHost(string Checkout, string Home, string WorkingDirectory, string ExternalClones, bool Windows);

/// <summary>
/// <c>rig skills</c>: keeps the agent CLIs' global skills folders linked to the curated catalog, overlays extra
/// skills reversibly, and vendors upstream skills.
/// </summary>
/// <param name="host">Where it reads and writes.</param>
/// <param name="git">Runs git for vendor clones.</param>
/// <param name="linker">Makes directory links.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where failures are reported.</param>
[CliCommand(Name = "skills", Description = "Manage global AI agent skill symlinks across OpenCode, Claude, Codex, and Antigravity.")]
internal sealed class SkillsCommand(SkillsHost host, Git git, IDirectoryLinker linker, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal SkillsCommand()
        : this(null!, null!, null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The home folder to use instead of the host's; every subcommand reads it.</summary>
    [CliOption(Name = "--home", Recursive = true, Required = false, Description = "Home folder holding the agents' skills folders (default: yours).")]
    public string? Home { get; set; }

    /// <summary>The <c>skills</c> command.</summary>
    public Command Build()
    {
        var command = DeclarativeCommand.BuildGroup(this);
        command.Add(DeclarativeCommand.Build(new StatusCommand(this)));
        command.Add(DeclarativeCommand.Build(new SyncCommand(this)));
        command.Add(DeclarativeCommand.Build(new ActivateCommand(this)));
        command.Add(DeclarativeCommand.Build(new DeactivateCommand(this)));
        command.Add(DeclarativeCommand.Build(new PruneCommand(this)));
        command.Add(DeclarativeCommand.Build(new UpdateCommand(this)));
        command.Add(DeclarativeCommand.Build(new ListCommand(this)));
        return command;
    }

    // The group's recursive --home, read from whichever subcommand was run.
    private static string? HomeOf(CliContext context) => context.Result.ParseResult.GetValue<string>("--home");

    private int Deactivate(string? homeOverride, string[] only, bool all, string[] names, bool dryRun) =>
        Guarded(homeOverride, places => Overlay(only, all, () =>
            WithSystems(names, systems =>
            {
                var links = new SkillLinks(places, linker);
                foreach (var (target, count) in links.Deactivate(SkillCatalog.At(places), only, all, systems, dryRun))
                {
                    output.WriteLine($"{SkillSystems.DisplayName(target)}: removed {count} link(s)");
                }

                return 0;
            })));

    private int Prune(string? homeOverride, string[] names, bool dryRun) =>
        Guarded(homeOverride, places => WithSystems(names, systems =>
        {
            var pruned = new SkillLinks(places, linker).PruneBroken(systems, dryRun);
            var total = pruned.Sum(p => p.Count);
            if (total == 0)
            {
                output.WriteLine("✓ No broken symlinks found across target systems.");
                return 0;
            }

            foreach (var (target, count) in pruned)
            {
                output.WriteLine($"  • {SkillSystems.DisplayName(target)}: {(dryRun ? "Would prune" : "Pruned")} {count} broken symlink(s)");
            }

            output.WriteLine();
            output.WriteLine($"✓ Done ({total} broken symlink(s) handled).");
            return 0;
        }));

    private async Task<int> Update(string? homeOverride, string[] names, string? vendor, bool sync, CancellationToken ct)
    {
        var places = Places(homeOverride);
        if (SkillSystems.Resolve(names, out var unknown) is not { } systems)
        {
            return UnknownSystem(unknown);
        }

        try
        {
            return await UpdateAsync(places, systems, vendor, sync, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsReported(e))
        {
            await error.WriteLineAsync($"rig skills: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private int List(string? homeOverride, string? query) =>
        Guarded(homeOverride, places =>
        {
            var skills = SkillCatalog.At(places).Curated();
            if (query is { Length: > 0 } text)
            {
                var q = UnicodeText.Lower(text);
                skills = [.. skills.Where(s => UnicodeText.Lower(s.Name).Contains(q, StringComparison.Ordinal) || UnicodeText.Lower(s.Description).Contains(q, StringComparison.Ordinal))];
            }

            var table = new TextTable($"Skills Catalog ({skills.Count} total)", [("Skill Name", false), ("Source", false), ("Description", false)]);
            foreach (var skill in skills)
            {
                var description = UnicodeText.Length(skill.Description) > 60 ? UnicodeText.Prefix(skill.Description, 60) + "..." : skill.Description;
                _ = table.Add(skill.Name, skill.Source, description);
            }

            output.Write(table.Render());
            return 0;
        });

    internal int Status(string? homeOverride, string[] names) =>
        Guarded(homeOverride, places => WithSystems(names, systems => Status(places, systems)));

    private int Status(SkillPlaces places, IReadOnlyList<string> systems)
    {
        var catalog = SkillCatalog.At(places);
        var skills = catalog.Curated();
        var audits = new SkillLinks(places, linker).Audit(skills, systems);
        var allowlist = catalog.EstateAllowlist is null ? string.Empty : ", allowlist active";
        var table = new TextTable(
            $"AI Agent Skills Symlink Verification ({skills.Count} active skills{allowlist})",
            [("CLI System", false), ("Directory Path", false), ("Status", false), ("Active", true), ("Issues", false)]);
        foreach (var audit in audits)
        {
            var (badge, issues) = audit switch
            {
                { Exists: false } => ("Missing Dir", "Target directory does not exist"),
                { Healthy: true } => ("✓ Healthy", "None"),
                _ => ("! Drifted", Issues(audit)),
            };
            _ = table.Add(SkillSystems.DisplayName(audit.System), audit.Folder, badge, $"{audit.Valid.Count}/{skills.Count}", issues);
        }

        var activated = new SkillLinks(places, linker).Activated();
        var rendered = table.Render().Split('\n', 2);
        output.WriteLine(rendered[0]);
        if (activated.Count > 0)
        {
            output.WriteLine($"Temporary overlay: {activated.Count} skill(s) active; run 'rig skills deactivate --all' to restore the curated catalog.");
        }

        output.WriteLine();
        output.Write(rendered[1]);
        output.WriteLine();
        output.WriteLine(audits.All(a => a.Healthy)
            ? "✓ All AI agent systems are completely in sync!"
            : "Run 'rig skills sync' to provision or fix missing symlinks.");
        return 0;
    }

    private static string Issues(SystemSkillsAudit audit) =>
        string.Join(", ", new (int Count, string Label)[]
        {
            (audit.Missing.Count, "missing"),
            (audit.Broken.Count, "broken"),
            (audit.Stale.Count, "stale"),
            (audit.Drifted.Count, "drifted"),
            (audit.Collisions.Count, "non-symlink collision"),
        }.Where(i => i.Count > 0).Select(i => $"{i.Count} {i.Label}"));

    internal int Sync(string? homeOverride, string[] names, string[] only, bool all, bool prune, bool dryRun) =>
        Guarded(homeOverride, places => WithSystems(names, systems => Sync(places, systems, only, all, prune, dryRun)));

    private int Sync(SkillPlaces places, IReadOnlyList<string> systems, string[] only, bool all, bool prune, bool dryRun)
    {
        if (all && only.Length > 0)
        {
            output.WriteLine("Use either --all or --only, not both.");
            return 2;
        }

        if (all || only.Length > 0)
        {
            return Activated(places, systems, only, all, dryRun, announce: true);
        }

        var skills = SkillCatalog.At(places).Curated();
        if (dryRun)
        {
            output.WriteLine("Running in dry-run mode (no changes will be applied)");
            output.WriteLine();
        }

        output.WriteLine($"Reconciling {skills.Count} selected skills across {systems.Count} CLI system(s):");
        foreach (var system in systems)
        {
            output.WriteLine($"  • {SkillSystems.DisplayName(system)}");
        }

        output.WriteLine();
        foreach (var (system, count) in new SkillLinks(places, linker).Sync(skills, systems, prune, dryRun))
        {
            output.WriteLine($"✓ {SkillSystems.DisplayName(system)}: {(dryRun ? "Would link" : "Successfully linked")} {count} skill(s)");
        }

        output.WriteLine();
        if (!dryRun)
        {
            output.WriteLine("✓ Symlink synchronization complete across all requested systems!");
        }

        return 0;
    }

    internal int Activate(string? homeOverride, string[] names, string[] only, bool all, bool dryRun) =>
        Guarded(homeOverride, places => Overlay(only, all, () =>
            WithSystems(names, systems => Activated(places, systems, only, all, dryRun, announce: false))));

    private int Activated(SkillPlaces places, IReadOnlyList<string> systems, IReadOnlyList<string> only, bool all, bool dryRun, bool announce)
    {
        IReadOnlyList<SystemCount> results;
        try
        {
            results = new SkillLinks(places, linker).Activate(SkillCatalog.At(places), only, all, systems, dryRun);
        }
        catch (ArgumentException e)
        {
            output.WriteLine(e.Message);
            return 2;
        }

        if (announce)
        {
            output.WriteLine($"Activating {(all ? "all discovered" : string.Join(", ", only))} as a reversible skill overlay:");
        }

        foreach (var (system, count) in results)
        {
            output.WriteLine($"{(announce ? "  " : string.Empty)}{SkillSystems.DisplayName(system)}: {count} link(s)");
        }

        if (announce && !dryRun)
        {
            output.WriteLine("Run 'rig skills deactivate --all' to restore the curated catalog.");
        }

        return 0;
    }

    private async Task<int> UpdateAsync(SkillPlaces places, IReadOnlyList<string> systems, string? only, bool sync, CancellationToken ct)
    {
        await output.WriteLineAsync("Fetching & Updating Upstream Vendored Skills").ConfigureAwait(false);
        var vendors = SkillVendors.Load(places.Vendors);
        if (only is not null)
        {
            vendors = [.. vendors.Where(v => v.Name == only)];
            if (vendors.Count == 0)
            {
                await output.WriteLineAsync($"Error: Vendor '{only}' not found in configuration.").ConfigureAwait(false);
                return 1;
            }
        }

        var results = await new SkillVendors(git, host.ExternalClones, places.Vendored, places.Windows)
            .UpdateAsync(vendors, output.WriteLine, ct).ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"✓ Successfully vendored {results.Sum(r => r.Skills.Count)} skills across {results.Count} repos.").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        if (!sync)
        {
            return 0;
        }

        foreach (var (system, count) in new SkillLinks(places, linker).Sync(SkillCatalog.At(places).Curated(), systems, prune: true, dryRun: false))
        {
            await output.WriteLineAsync($"✓ {SkillSystems.DisplayName(system)}: Synced {count} skill(s)").ConfigureAwait(false);
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("✓ Skills vendoring and global symlink sync complete!").ConfigureAwait(false);
        return 0;
    }

    // activate and deactivate take exactly one of --only and --all.
    private int Overlay(string[] only, bool all, Func<int> run)
    {
        if (all == only.Length > 0)
        {
            output.WriteLine("Choose exactly one of --only or --all.");
            return 2;
        }

        return run();
    }

    private int WithSystems(string[] names, Func<IReadOnlyList<string>, int> run) =>
        SkillSystems.Resolve(names, out var unknown) is { } systems ? run(systems) : UnknownSystem(unknown);

    private int UnknownSystem(string? name)
    {
        output.WriteLine($"Error: Unknown system '{name}'. Supported systems: {string.Join(", ", SkillSystems.All)}");
        return 1;
    }

    private SkillPlaces Places(string? homeOverride) =>
        new(host.Checkout, homeOverride ?? host.Home, host.WorkingDirectory, host.Windows);

    // A filesystem or registry failure is reported, not thrown.
    private int Guarded(string? homeOverride, Func<SkillPlaces, int> run)
    {
        try
        {
            return run(Places(homeOverride));
        }
        catch (Exception e) when (IsReported(e))
        {
            error.WriteLine($"rig skills: {e.Message}");
            return 1;
        }
    }

    private static bool IsReported(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;

    /// <summary><c>rig skills status</c>.</summary>
    [CliCommand(Name = "status", Description = "Audit the current symlink state across AI agent CLI systems.")]
    internal sealed class StatusCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal StatusCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Filter audit to specific CLI systems (opencode, claude, codex, antigravity, agents, all).")]
        public string[] Systems { get; set; } = [];

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.Status(HomeOf(context), Systems);
    }

    /// <summary><c>rig skills sync</c>.</summary>
    [CliCommand(Name = "sync", Description = "Synchronize skill symlinks across supported agent CLIs.")]
    internal sealed class SyncCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal SyncCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Specific CLI systems to sync (opencode, claude, codex, antigravity, agents, all).")]
        public string[] Systems { get; set; } = [];

        /// <summary>Accepted for symmetry; pruning is the default.</summary>
        [CliOption(Name = "--prune", Description = "Remove links outside the catalog (the default).")]
        public bool Prune { get; set; }

        /// <summary>Keeps links outside the catalog.</summary>
        [CliOption(Name = "--no-prune", Description = "Keep links outside the catalog.")]
        public bool NoPrune { get; set; }

        /// <summary>Previews without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Preview links without modifying disk.")]
        public bool DryRun { get; set; }

        /// <summary>Includes every discovered skill.</summary>
        [CliOption(Name = "--all", Description = "Temporarily include every discovered skill; does not change the permanent allowlist.")]
        public bool All { get; set; }

        /// <summary>Skill names.</summary>
        [CliOption(Name = "--only", Required = false, Description = "Temporarily provision named skills; repeat the option for multiple skills.")]
        public string[] Only { get; set; } = [];

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.Sync(HomeOf(context), Systems, Only, All, !NoPrune, DryRun);
    }

    /// <summary><c>rig skills activate</c>.</summary>
    [CliCommand(Name = "activate", Description = "Activate a temporary skill overlay that can later be deactivated.")]
    internal sealed class ActivateCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ActivateCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Target CLI systems.")]
        public string[] Systems { get; set; } = [];

        /// <summary>Skill names.</summary>
        [CliOption(Name = "--only", Required = false, Description = "Skill names to expose; repeat for multiple skills.")]
        public string[] Only { get; set; } = [];

        /// <summary>Exposes every discovered skill.</summary>
        [CliOption(Name = "--all", Description = "Expose every discovered skill.")]
        public bool All { get; set; }

        /// <summary>Previews without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Preview links without changing disk.")]
        public bool DryRun { get; set; }

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.Activate(HomeOf(context), Systems, Only, All, DryRun);
    }

    /// <summary><c>rig skills deactivate</c>.</summary>
    [CliCommand(Name = "deactivate", Description = "Remove temporary skill links and restore the curated catalog.")]
    internal sealed class DeactivateCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DeactivateCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Target CLI systems.")]
        public string[] Systems { get; set; } = [];

        /// <summary>Skill names.</summary>
        [CliOption(Name = "--only", Required = false, Description = "Overlay names to remove; repeat for multiple skills.")]
        public string[] Only { get; set; } = [];

        /// <summary>Removes every overlay.</summary>
        [CliOption(Name = "--all", Description = "Remove every recorded overlay.")]
        public bool All { get; set; }

        /// <summary>Previews without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Preview removals without changing disk.")]
        public bool DryRun { get; set; }

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.Deactivate(HomeOf(context), Only, All, Systems, DryRun);
    }

    /// <summary><c>rig skills prune</c>.</summary>
    [CliCommand(Name = "prune", Description = "Prune broken or dangling symlinks across AI agent CLI systems.")]
    internal sealed class PruneCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal PruneCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Specific CLI systems to prune (opencode, claude, codex, antigravity, agents, all).")]
        public string[] Systems { get; set; } = [];

        /// <summary>Previews without writing.</summary>
        [CliOption(Name = "--dry-run", Description = "Preview links without removing them.")]
        public bool DryRun { get; set; }

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.Prune(HomeOf(context), Systems, DryRun);
    }

    /// <summary><c>rig skills update</c>.</summary>
    [CliCommand(Name = "update", Description = "Fetch or update vendored upstream skills, then sync the links.")]
    internal sealed class UpdateCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal UpdateCommand()
            : this(null!)
        {
        }

        /// <summary>The systems to act on; none is all of them.</summary>
        [CliOption(Name = "--system", Aliases = ["-s"], Required = false, AllowMultipleArgumentsPerToken = false, Description = "Specific CLI systems to sync (opencode, claude, codex, antigravity, agents, all).")]
        public string[] Systems { get; set; } = [];

        /// <summary>Limits the update to one vendor.</summary>
        [CliOption(Name = "--vendor", Aliases = ["-v"], Required = false, Description = "Update only this upstream vendor.")]
        public string? Vendor { get; set; }

        /// <summary>Skips the link sync.</summary>
        [CliOption(Name = "--no-sync", Description = "Leave the systems' links alone after updating vendored skills.")]
        public bool NoSync { get; set; }

        /// <summary>The handler.</summary>
        public Task<int> RunAsync(CliContext context) => skills.Update(HomeOf(context), Systems, Vendor, !NoSync, context.CancellationToken);
    }

    /// <summary><c>rig skills list</c>.</summary>
    [CliCommand(Name = "list", Description = "List the curated skills discovered in the repository, personal overrides and vendors.")]
    internal sealed class ListCommand(SkillsCommand skills)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ListCommand()
            : this(null!)
        {
        }

        /// <summary>Filters by name or description.</summary>
        [CliOption(Name = "--query", Aliases = ["-q"], Required = false, Description = "Show only skills whose name or description contains this.")]
        public string? Query { get; set; }

        /// <summary>The handler.</summary>
        public int Run(CliContext context) => skills.List(HomeOf(context), Query);
    }
}
