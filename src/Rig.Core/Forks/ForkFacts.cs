namespace Rig.Core.Forks;

/// <summary>One commit that is not a merge.</summary>
/// <param name="Name">The author's name, after the repository's mailmap.</param>
/// <param name="Email">The author's email, after the repository's mailmap.</param>
/// <param name="Date">The author date.</param>
/// <param name="SignedOff">Whether the commit carries a Signed-off-by trailer.</param>
public sealed record CommitFact(string Name, string Email, DateOnly Date, bool SignedOff);

/// <summary>A file whose text carries a licence marker.</summary>
/// <param name="Path">The file.</param>
/// <param name="Spdx">The licence the marker names.</param>
public sealed record HeaderHit(string Path, string Spdx);

/// <summary>A ref holding commits the assessed revision does not.</summary>
/// <param name="Name">The ref, short form.</param>
/// <param name="UniqueCommits">How many commits, merges aside, only it holds.</param>
/// <param name="LastDate">Its tip's commit date.</param>
public sealed record RefFact(string Name, int UniqueCommits, DateOnly LastDate);

/// <summary>Lines at the assessed revision last changed by one author.</summary>
/// <param name="Name">The author's name.</param>
/// <param name="Email">The author's email.</param>
/// <param name="Lines">How many lines.</param>
public sealed record BlameFact(string Name, string Email, int Lines);

/// <summary>Everything the assessment reads from one revision of a clone. Nothing here depends on the clock or the network.</summary>
/// <param name="Revision">The assessed commit.</param>
/// <param name="RevisionDate">Its commit date: the "now" every window is measured back from.</param>
/// <param name="Shallow">Whether the clone's history is cut short.</param>
/// <param name="Commits">Every non-merge commit reachable from the revision.</param>
/// <param name="Merges">How many merge commits are reachable from it.</param>
/// <param name="Tags">How many tags it contains.</param>
/// <param name="Files">Every path at the revision.</param>
/// <param name="Texts">The text of each file the configuration asks to read, by path.</param>
/// <param name="HeaderHits">Files carrying a licence marker.</param>
/// <param name="Refs">Refs holding work the revision does not.</param>
/// <param name="Blame">Lines per author, when blame was asked for.</param>
public sealed record ForkFacts(
    string Revision,
    DateOnly RevisionDate,
    bool Shallow,
    IReadOnlyList<CommitFact> Commits,
    int Merges,
    int Tags,
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, string> Texts,
    IReadOnlyList<HeaderHit> HeaderHits,
    IReadOnlyList<RefFact> Refs,
    IReadOnlyList<BlameFact>? Blame);
