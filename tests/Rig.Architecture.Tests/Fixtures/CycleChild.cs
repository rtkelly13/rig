namespace Rig.Architecture.Tests.Fixtures.CycleParent.Child;

/// <summary>The other half of the parent/child cycle in <see cref="Parent"/>.</summary>
public sealed record Kid(Parent? Parent);
