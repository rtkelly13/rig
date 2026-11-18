using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;
using Rig.Core.Tests.Vault;

namespace Rig.Cli.Tests.Vault;

/// <summary><c>rig vault drive-backup</c> against real git (<see cref="DriveVaults"/>).</summary>
public sealed class DriveBackupCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_dry_run_lists_the_differing_notes_and_reports_drift()
    {
        await ArrangeAsync(new() { ["Same.md"] = "same", ["Edited.md"] = "drive edit" });

        var exit = await RunAsync("vault", "drive-backup");

        Assert.Equal(1, exit);
        Assert.Contains("1 of 2 shared note(s) on Drive differ from main", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("  Edited.md", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("pass --apply", _out.ToString(), StringComparison.Ordinal);
        Assert.Empty(await Repos.GitAsync(Origin, "branch", "--list", "backup/*"));
    }

    [Fact]
    public async Task Apply_pushes_a_stamped_backup_branch()
    {
        await ArrangeAsync(new() { ["Edited.md"] = "drive edit" });

        var exit = await RunAsync("vault", "drive-backup", "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("Pushed backup/drive-diff-20261117-225258", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal("drive edit", await Repos.GitAsync(Origin, "show", "backup/drive-diff-20261117-225258:Vault/Edited.md"));
    }

    [Fact]
    public async Task Nothing_differing_is_clean()
    {
        await ArrangeAsync(new() { ["Same.md"] = "same" });

        var exit = await RunAsync("vault", "drive-backup", "--apply");

        Assert.Equal(0, exit);
        Assert.Contains("Nothing to back up.", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_failure_exits_with_an_error()
    {
        await ArrangeAsync(new() { ["Edited.md"] = "drive edit" });
        _ = await Repos.GitAsync(Path.Join(_temp.Path, "vault-repo"), "remote", "set-url", "origin", Path.Join(_temp.Path, "missing.git"));

        var exit = await RunAsync("vault", "drive-backup");

        Assert.Equal(2, exit);
        Assert.Contains("fetch main failed", _err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Drive placeholder", _err.ToString() + _out, StringComparison.Ordinal);
    }

    private string Origin => Path.Join(_temp.Path, "origin.git");

    private async Task ArrangeAsync(Dictionary<string, string> drive)
    {
        var (_, vault, driveVault) = await DriveVaults.ArrangeAsync(_temp, drive);
        _environment["VAULT_PATH"] = vault;
        _environment["DRIVE_VAULT_PATH"] = driveVault;
    }

    private Task<int> RunAsync(params string[] args)
    {
        var host = new VaultHost(v => _environment.GetValueOrDefault(v), _temp.Path);
        var command = new VaultCommand(
            Repos.Proc,
            host,
            new KnowledgeServerCommand(Repos.Proc, _ => throw new InvalidOperationException("not used"), false, _ => null, _out, _err),
            new DriveBackupCommand(new Git(Repos.Proc, _temp.Path), host, new FixedTime(new DateTimeOffset(2026, 11, 17, 22, 52, 58, TimeSpan.Zero)), _out, _err),
            _out,
            _err);
        return command.Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, Token);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
