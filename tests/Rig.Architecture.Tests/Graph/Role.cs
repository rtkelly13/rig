namespace Rig.Architecture.Tests.Graph;

/// <summary>
/// The five bands of <c>docs/target-architecture.md</c> § Layering, lowest first: references may only point to the
/// same role or a lower one. <see cref="None"/> is a namespace nobody has placed yet, which is itself a failure.
/// </summary>
public enum Role
{
    None = 0,
    Leaf = 1,
    Domain = 2,
    Adapter = 3,
    Feature = 4,
    CompositionRoot = 5,
}
