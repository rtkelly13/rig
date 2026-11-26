namespace Rig.Architecture.Tests.Graph;

/// <summary>How a reference from one namespace to another sits against the layering.</summary>
public enum EdgeKind
{
    None = 0,

    /// <summary>Down a role, or from a composition root, which may reference anything.</summary>
    Allowed = 1,

    /// <summary>Inside one area, such as a platform adapter implementing a port its own feature declares.</summary>
    WithinArea = 2,

    /// <summary>Into another feature's area. Features are independent; a shared need moves down a layer.</summary>
    CrossesFeature = 3,

    /// <summary>To a higher role, such as an adapter reaching a feature or a feature reaching a composition root.</summary>
    PointsUp = 4,
}
