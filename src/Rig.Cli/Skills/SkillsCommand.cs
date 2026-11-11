using System.CommandLine;
using Rig.Cli.Vault;
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
/// skills reversibly, and vendors upstream skills. Ported from <c>rig.skills.cli</c> (<c>workspace skills</c>).
/// </summary>
/// <param name="host">Where it reads and writes.</param>
/// <param name="git">Runs git for vendor clones.</param>
/// <param name="linker">Makes directory links.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where failures are reported.</param>
internal sealed class SkillsCommand(SkillsHost host, Git git, IDirectoryLinker linker, TextWriter output, TextWriter error)
{
    private readonly Option<string> home = new("--home")
    {
        Description = "Home folder holding the agents' skills folders (default: yours).",
        Recursive = true,
    };

    /// <summary>The <c>skills</c> command.</summary>
    public Command Build()
    {
        var command = new Command("skills", "Manage global AI agent skill symlinks across OpenCode, Claude, Codex, and Antigravity.")
        {
            Status(), Sync(), Activate(), Deactivate(), Prune(), Update(), List(),
        };
        command.Options.Add(home);
        return command;
    }

    private static Option<string[]> Systems(string description) =>
        new("--system", "-s") { Description = description, AllowMultipleArgumentsPerToken = false };

    private static Option<bool> Flag(string name, string description) => new(name) { Description = description };

    private static Option<string[]> Only(string description) => new("--only") { Description = description };

    private Command Status()
    {
        var system = Systems("Filter audit to specific CLI systems (opencode, claude, codex, antigravity, agents, all).");
        var command = new Command("status", "Audit the current symlink state across AI agent CLI systems.") { system };
        command.SetAction(parsed => Guarded(parsed, places => WithSystems(parsed.GetValue(system), systems => Status(places, systems))));
        return command;
    }

    private Command Sync()
    {
        var system = Systems("Specific CLI systems to sync (opencode, claude, codex, antigravity, agents, all).");
        var prune = Flag("--prune", "Remove links outside the catalog (the default).");
        var noPrune = Flag("--no-prune", "Keep links outside the catalog.");
        var dryRun = Flag("--dry-run", "Preview links without modifying disk.");
        var all = Flag("--all", "Temporarily include every discovered skill; does not change the permanent allowlist.");
        var only = Only("Temporarily provision named skills; repeat the option for multiple skills.");
        var command = new Command("sync", "Synchronize skill symlinks across supported agent CLIs.") { system, prune, noPrune, dryRun, all, only };
        command.SetAction(parsed => Guarded(parsed, places => WithSystems(
            parsed.GetValue(system),
            systems => Sync(places, systems, parsed.GetValue(only) ?? [], parsed.GetValue(all), !parsed.GetValue(noPrune), parsed.GetValue(dryRun)))));
        return command;
    }

    private Command Activate()
    {
        var (system, only, all, dryRun) = OverlayOptions("Skill names to expose; repeat for multiple skills.", "Expose every discovered skill.", "Preview links without changing disk.");
        var command = new Command("activate", "Activate a temporary skill overlay that can later be deactivated.") { system, only, all, dryRun };
        command.SetAction(parsed => Guarded(parsed, places => Overlay(parsed.GetValue(only) ?? [], parsed.GetValue(all), () =>
            WithSystems(parsed.GetValue(system), systems => Activated(places, systems, parsed.GetValue(only) ?? [], parsed.GetValue(all), parsed.GetValue(dryRun), announce: false)))));
        return command;
    }

    private Command Deactivate()
    {
        var (system, only, all, dryRun) = OverlayOptions("Overlay names to remove; repeat for multiple skills.", "Remove every recorded overlay.", "Preview removals without changing disk.");
        var command = new Command("deactivate", "Remove temporary skill links and restore the curated catalog.") { system, only, all, dryRun };
        command.SetAction(parsed => Guarded(parsed, places => Overlay(parsed.GetValue(only) ?? [], parsed.GetValue(all), () =>
            WithSystems(parsed.GetValue(system), systems =>
            {
                var links = new SkillLinks(places, linker);
                foreach (var (target, count) in links.Deactivate(SkillCatalog.At(places), parsed.GetValue(only) ?? [], parsed.GetValue(all), systems, parsed.GetValue(dryRun)))
                {
                    output.WriteLine($"{SkillSystems.DisplayName(target)}: removed {count} link(s)");
                }

                return 0;
            }))));
        return command;
    }

    private static (Option<string[]> System, Option<string[]> Only, Option<bool> All, Option<bool> DryRun) OverlayOptions(string only, string all, string dryRun) =>
        (Systems("Target CLI systems."), Only(only), Flag("--all", all), Flag("--dry-run", dryRun));

    private Command Prune()
    {
        var system = Systems("Specific CLI systems to prune (opencode, claude, codex, antigravity, agents, all).");
        var dryRun = Flag("--dry-run", "Preview links without removing them.");
        var command = new Command("prune", "Prune broken or dangling symlinks across AI agent CLI systems.") { system, dryRun };
        command.SetAction(parsed => Guarded(parsed, places => WithSystems(parsed.GetValue(system), systems =>
        {
            var pruned = new SkillLinks(places, linker).PruneBroken(systems, parsed.GetValue(dryRun));
            var total = pruned.Sum(p => p.Count);
            if (total == 0)
            {
                output.WriteLine("✓ No broken symlinks found across target systems.");
                return 0;
            }

            foreach (var (target, count) in pruned)
            {
                output.WriteLine($"  • {SkillSystems.DisplayName(target)}: {(parsed.GetValue(dryRun) ? "Would prune" : "Pruned")} {count} broken symlink(s)");
            }

            output.WriteLine();
            output.WriteLine($"✓ Done ({total} broken symlink(s) handled).");
            return 0;
        })));
        return command;
    }

    private Command Update()
    {
        var system = Systems("Specific CLI systems to sync (opencode, claude, codex, antigravity, agents, all).");
        var vendor = new Option<string>("--vendor", "-v") { Description = "Update only this upstream vendor." };
        var noSync = Flag("--no-sync", "Leave the systems' links alone after updating vendored skills.");
        var command = new Command("update", "Fetch or update vendored upstream skills, then sync the links.") { system, vendor, noSync };
        command.SetAction(async (parsed, ct) =>
        {
            var places = Places(parsed);
            if (SkillSystems.Resolve(parsed.GetValue(system) ?? [], out var unknown) is not { } systems)
            {
                return UnknownSystem(unknown);
            }

            try
            {
                return await UpdateAsync(places, systems, parsed.GetValue(vendor), !parsed.GetValue(noSync), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (IsReported(e))
            {
                await error.WriteLineAsync($"rig skills: {e.Message}").ConfigureAwait(false);
                return 1;
            }
        });
        return command;
    }

    private Command List()
    {
        var query = new Option<string>("--query", "-q") { Description = "Show only skills whose name or description contains this." };
        var command = new Command("list", "List the curated skills discovered in the repository, personal overrides and vendors.") { query };
        command.SetAction(parsed => Guarded(parsed, places =>
        {
            var skills = SkillCatalog.At(places).Curated();
            if (parsed.GetValue(query) is { Length: > 0 } text)
            {
                var q = PyText.Lower(text);
                skills = [.. skills.Where(s => PyText.Lower(s.Name).Contains(q, StringComparison.Ordinal) || PyText.Lower(s.Description).Contains(q, StringComparison.Ordinal))];
            }

            var table = new TextTable($"Skills Catalog ({skills.Count} total)", [("Skill Name", false), ("Source", false), ("Description", false)]);
            foreach (var skill in skills)
            {
                var description = PyText.Length(skill.Description) > 60 ? PyText.Prefix(skill.Description, 60) + "..." : skill.Description;
                _ = table.Add(skill.Name, skill.Source, description);
            }

            output.Write(table.Render());
            return 0;
        }));
        return command;
    }

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

    private int WithSystems(string[]? names, Func<IReadOnlyList<string>, int> run) =>
        SkillSystems.Resolve(names ?? [], out var unknown) is { } systems ? run(systems) : UnknownSystem(unknown);

    private int UnknownSystem(string? name)
    {
        output.WriteLine($"Error: Unknown system '{name}'. Supported systems: {string.Join(", ", SkillSystems.All)}");
        return 1;
    }

    private SkillPlaces Places(ParseResult parsed) =>
        new(host.Checkout, parsed.GetValue(home) ?? host.Home, host.WorkingDirectory, host.Windows);

    // A filesystem or registry failure is reported, not thrown: the Python raised it as a traceback and exit 1.
    private int Guarded(ParseResult parsed, Func<SkillPlaces, int> run)
    {
        try
        {
            return run(Places(parsed));
        }
        catch (Exception e) when (IsReported(e))
        {
            error.WriteLine($"rig skills: {e.Message}");
            return 1;
        }
    }

    private static bool IsReported(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException;
}
