using Rig.Architecture.Tests.Fixtures.CycleParent.Child;

namespace Rig.Architecture.Tests.Fixtures.CycleParent;

/// <summary>Half of a deliberate parent/child namespace cycle that <see cref="CycleTests"/> must catch.</summary>
public sealed record Parent(Kid? Kid);
