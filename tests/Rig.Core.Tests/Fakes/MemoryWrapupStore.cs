using Rig.Core.Wrapups;

namespace Rig.Core.Tests.Fakes;

/// <summary>State files in memory, keyed by worktree path; <see cref="Refuse"/> makes every write fail with that reason.</summary>
internal sealed class MemoryWrapupStore : IWrapupStore
{
    public Dictionary<string, WrapupState> Files { get; } = new(StringComparer.Ordinal);

    public string? Refuse { get; set; }

    /// <summary>Rewrites each state as it is written, as an agent reporting at that moment would.</summary>
    public Func<WrapupState, WrapupState>? OnWrite { get; set; }

    public WrapupState? Read(string worktree) => Files.GetValueOrDefault(worktree);

    public Task<string?> WriteAsync(string worktree, WrapupState state, CancellationToken cancellationToken = default)
    {
        if (Refuse is null)
        {
            Files[worktree] = OnWrite?.Invoke(state) ?? state;
        }

        return Task.FromResult(Refuse);
    }
}
