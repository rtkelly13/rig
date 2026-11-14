using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// <see cref="WorktreeRemovalJudge"/> from tables: one row per fact and policy, no disk and no git. The commands share
/// this decision, so a gap here is a gap in every removal path at once.
/// </summary>
public sealed class WorktreeRemovalJudgeTests
{
    private static readonly IReadOnlyDictionary<string, RemovalPolicy> Policies = new Dictionary<string, RemovalPolicy>(StringComparer.Ordinal)
    {
        ["strict"] = new RemovalPolicy(),
        ["discard-ignored"] = new RemovalPolicy(DiscardIgnoredNotes: true),
        ["force"] = new RemovalPolicy(DiscardUnpushedCommits: true),
        ["everything"] = new RemovalPolicy(DiscardIgnoredNotes: true, DiscardUnpushedCommits: true),
    };

    // Every fact that can refuse, and the cleanup commands' policies against it. A command has no flag for a hard
    // reason: the fact may be lying, a person is using the tree, or the work exists nowhere else.
    public static TheoryData<string, string, bool> Matrix()
    {
        var rows = new TheoryData<string, string, bool>();
        foreach (var (fact, expected) in new[]
        {
            ("clean", new[] { true, true, true, true }),
            ("in-use", new[] { false, false, false, false }),
            ("ignored-notes", new[] { false, true, false, true }),
            ("ignored-unreadable", new[] { false, true, false, true }),
            ("unborn", new[] { false, false, false, false }),
            ("foreign", new[] { false, false, false, false }),
            ("ownership-unreadable", new[] { false, false, false, false }),
            ("locked", new[] { false, false, false, false }),
            ("reflog-only", new[] { false, false, false, false }),
            ("reflog-unreadable", new[] { false, false, false, false }),
            ("unpushed", new[] { false, false, true, true }),
        })
        {
            var policies = new[] { "strict", "discard-ignored", "force", "everything" };
            for (var index = 0; index < policies.Length; index++)
            {
                rows.Add(fact, policies[index], expected[index]);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_fact_has_one_answer_per_policy(string fact, string policy, bool remove)
    {
        var decision = WorktreeRemovalJudge.Judge(Facts(fact), Policies[policy]);

        Assert.Equal(remove, decision.Remove);
        Assert.Equal(remove, decision.Reasons.Count == 0);
    }

    [Fact]
    public void Every_refusal_names_its_reason_and_reasons_keep_the_documented_order()
    {
        var facts = Facts("clean") with
        {
            InUse = true,
            HasIgnoredNotes = true,
            IgnoredNotes = ["TODO.md"],
            IsUnborn = true,
            IsForeignClone = true,
            IsLocked = true,
            LockReason = "on a usb stick",
            ReflogOnlyCommitCount = 2,
            UnpushedCommitCount = 3,
        };

        var decision = WorktreeRemovalJudge.Judge(facts, RemovalPolicy.Strict);

        Assert.False(decision.Remove);
        Assert.Equal(
            [
                RemovalRefusalReason.InUse,
                RemovalRefusalReason.IgnoredNotes,
                RemovalRefusalReason.UnbornHead,
                RemovalRefusalReason.ForeignClone,
                RemovalRefusalReason.Locked,
                RemovalRefusalReason.ReflogOnlyCommit,
                RemovalRefusalReason.UnpushedCommits,
            ],
            decision.Reasons);
    }

    [Fact]
    public void A_clean_worktree_is_allowed_under_every_policy()
    {
        foreach (var policy in Policies.Values)
        {
            Assert.Same(RemovalDecision.Allowed, WorktreeRemovalJudge.Judge(Facts("clean"), policy));
        }
    }

    [Fact]
    public void An_unreadable_reflog_refuses_rather_than_counting_zero()
    {
        var decision = WorktreeRemovalJudge.Judge(Facts("clean") with { ReflogUnreadable = true }, new RemovalPolicy(DiscardIgnoredNotes: true, DiscardUnpushedCommits: true));

        Assert.Equal([RemovalRefusalReason.ReflogOnlyCommit], decision.Reasons);
    }

    private static WorktreeFacts Facts(string fact) => fact switch
    {
        "in-use" => new WorktreeFacts("/w", InUse: true),
        "ignored-notes" => new WorktreeFacts("/w", HasIgnoredNotes: true) { IgnoredNotes = ["TODO.md"] },
        "ignored-unreadable" => new WorktreeFacts("/w") { IgnoredUnreadable = true },
        "unborn" => new WorktreeFacts("/w", IsUnborn: true),
        "foreign" => new WorktreeFacts("/w", IsForeignClone: true),
        "ownership-unreadable" => new WorktreeFacts("/w") { OwnershipUnreadable = true },
        "locked" => new WorktreeFacts("/w", IsLocked: true, LockReason: "on a usb stick"),
        "reflog-only" => new WorktreeFacts("/w", ReflogOnlyCommitCount: 1),
        "reflog-unreadable" => new WorktreeFacts("/w") { ReflogUnreadable = true },
        "unpushed" => new WorktreeFacts("/w", UnpushedCommitCount: 2),
        _ => new WorktreeFacts("/w"),
    };
}
