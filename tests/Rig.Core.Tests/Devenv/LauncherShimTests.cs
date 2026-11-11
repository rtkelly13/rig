using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The agent launcher shims and profile block. Ported from <c>test_agent_launcher_setup.py</c>.</summary>
public sealed class LauncherShimTests : IDisposable
{
    private readonly TempDir _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void The_shims_run_rig_agent_from_the_checkouts_cli()
    {
        Assert.Equal(
            $"{LauncherShim.Header}\ndotnet run --project \"C:\\code\\personal\\rig\\src\\Rig.Cli\" -- agent @args\nexit $LASTEXITCODE\n",
            LauncherShim.PowerShell("C:\\code\\personal\\rig\\src\\Rig.Cli"));
        Assert.Equal(
            $"#!/usr/bin/env bash\n{LauncherShim.Header}\nexec dotnet run --project \"C:/code/personal/rig/src/Rig.Cli\" -- agent \"$@\"\n",
            LauncherShim.Posix("C:\\code\\personal\\rig\\src\\Rig.Cli"));
        Assert.Equal(Path.Join("/c", "src", "Rig.Cli"), LauncherShim.CliProject("/c"));
    }

    [Fact]
    public void The_blocks_define_an_agent_function_calling_the_shim()
    {
        Assert.Contains("function agent {\n    & 'C:\\code\\agent.ps1' @args\n}\n", LauncherShim.PowerShellBlock("C:\\code\\agent.ps1"), StringComparison.Ordinal);
        var posix = LauncherShim.PosixBlock("/home/user/code/agent.sh");
        Assert.StartsWith(LauncherShim.Begin + "\n", posix, StringComparison.Ordinal);
        Assert.Contains("agent() {\n    \"/home/user/code/agent.sh\" \"$@\"\n}\n", posix, StringComparison.Ordinal);
        Assert.EndsWith(LauncherShim.End, posix, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_are_escaped_for_their_quoting()
    {
        Assert.Contains("--project \"C:\\O'Brien\\`$x`\"q``\" --", LauncherShim.PowerShell("C:\\O'Brien\\$x\"q`"), StringComparison.Ordinal);
        Assert.Contains("--project \"/home/O'Brien/\\$x\\\"q\\`\" --", LauncherShim.Posix("/home/O'Brien/$x\"q`"), StringComparison.Ordinal);
        Assert.Contains("& 'C:\\O''Brien\\agent.ps1' @args", LauncherShim.PowerShellBlock("C:\\O'Brien\\agent.ps1"), StringComparison.Ordinal);
        Assert.Contains("    \"/home/\\$me/agent.sh\" \"$@\"", LauncherShim.PosixBlock("/home/$me/agent.sh"), StringComparison.Ordinal);
    }

    private const string Block = $"{LauncherShim.Begin}\nnew\n{LauncherShim.End}";

    [Theory]
    [InlineData("", "BLOCK\n")]
    [InlineData("\n\n", "BLOCK\n")]
    [InlineData("oh-my-posh init pwsh | Invoke-Expression\n", "oh-my-posh init pwsh | Invoke-Expression\n\nBLOCK\n")]
    [InlineData("before\n\nBEGIN\nold\nEND\n\n\nafter\n", "before\n\nBLOCK\n\nafter\n")]
    [InlineData("BEGIN\nold\nEND", "BLOCK\n")]
    [InlineData("before\nEND\nBEGIN\n", "before\nEND\nBEGIN\n\nBLOCK\n")]
    public void Upsert_replaces_the_marked_block_or_appends_one(string existing, string expected)
    {
        static string Expand(string text) =>
            text.Replace("BLOCK", Block, StringComparison.Ordinal)
                .Replace("BEGIN", LauncherShim.Begin, StringComparison.Ordinal)
                .Replace("END", LauncherShim.End, StringComparison.Ordinal);

        Assert.Equal(Expand(expected), LauncherShim.Upsert(Expand(existing), Block));
    }

    [Fact]
    public void The_posix_profile_is_zshrc_for_zsh_or_when_one_exists()
    {
        Assert.Equal(Path.Join(_home.Path, ".zshrc"), LauncherShim.PosixProfile(_home.Path, "/bin/zsh"));
        Assert.Equal(Path.Join(_home.Path, ".bashrc"), LauncherShim.PosixProfile(_home.Path, "/bin/bash"));
        Assert.Equal(Path.Join(_home.Path, ".bashrc"), LauncherShim.PosixProfile(_home.Path, null));
        _ = _home.File(".zshrc");
        Assert.Equal(Path.Join(_home.Path, ".zshrc"), LauncherShim.PosixProfile(_home.Path, "/bin/bash"));
    }

    private const string Profile = "Microsoft.PowerShell_profile.ps1";

    [Fact]
    public void The_powershell_profile_defaults_to_the_standard_path() =>
        Assert.Equal(Path.Join(_home.Path, "Documents", "PowerShell", Profile), LauncherShim.PowerShellProfile(_home.Path));

    [Fact]
    public void A_missing_home_gets_the_standard_path() =>
        Assert.Equal(Path.Join(_home.Path, "none", "Documents", "PowerShell", Profile), LauncherShim.PowerShellProfile(Path.Join(_home.Path, "none")));

    [Theory]
    [InlineData(new[] { "Documents/WindowsPowerShell" }, "Documents/WindowsPowerShell")]
    [InlineData(new[] { "OneDrive/Documents/WindowsPowerShell", "Documents/WindowsPowerShell" }, "OneDrive/Documents/WindowsPowerShell")]
    [InlineData(new[] { "Documents/PowerShell", "OneDrive/Documents/WindowsPowerShell" }, "Documents/PowerShell")]
    [InlineData(new[] { "Documents/PowerShell", "OneDrive/Documents/PowerShell" }, "OneDrive/Documents/PowerShell")]
    public void The_powershell_profile_prefers_onedrive_then_powershell_then_windows_powershell(string[] present, string chosen)
    {
        foreach (var folder in present)
        {
            _ = _home.File($"{folder}/{Profile}");
        }

        Assert.Equal(Path.GetFullPath(Path.Join(_home.Path, chosen, Profile)), Path.GetFullPath(LauncherShim.PowerShellProfile(_home.Path)));
    }

    [Fact]
    public async Task A_linked_worktree_resolves_to_its_main_checkout()
    {
        var main = _home.Folder("rig");
        _ = _home.Folder("rig/.git/worktrees/feature");
        var worktree = _home.Folder("rig/.worktrees/feature");
        await File.WriteAllTextAsync(Path.Join(worktree, ".git"), $"gitdir: {Path.Join(main, ".git", "worktrees", "feature")}\n", TestContext.Current.CancellationToken);
        var relative = _home.Folder("elsewhere/feature");
        await File.WriteAllTextAsync(Path.Join(relative, ".git"), "gitdir: ../../rig/.git/worktrees/feature", TestContext.Current.CancellationToken);
        var submodule = _home.Folder("sub");
        await File.WriteAllTextAsync(Path.Join(submodule, ".git"), "gitdir: ../rig/.git/modules/sub", TestContext.Current.CancellationToken);

        Assert.Equal(main, LauncherShim.MainCheckout(worktree));
        Assert.Equal(main, LauncherShim.MainCheckout(relative));
        Assert.Equal(main, LauncherShim.MainCheckout(main));
        Assert.Equal(submodule, LauncherShim.MainCheckout(submodule));
        Assert.Equal(Path.Join(_home.Path, "none"), LauncherShim.MainCheckout(Path.Join(_home.Path, "none")));
    }

    [Fact]
    public void Every_known_agent_has_a_distinct_id_and_command()
    {
        Assert.Equal(9, KnownAgents.All.Count);
        Assert.Equal(KnownAgents.All.Count, KnownAgents.All.Select(a => a.Id).Distinct().Count());
        Assert.Contains(new KnownAgent("github-copilot", "GitHub Copilot", "copilot"), KnownAgents.All);
    }
}
