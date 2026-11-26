namespace Rig.Architecture.Tests.Graph;

/// <summary>Classifies namespace edges against a <see cref="RoleMap"/>. Pure: no assemblies, so tests can feed it edges.</summary>
internal static class Layering
{
    public static EdgeKind Classify(RoleMap roles, string from, string to)
    {
        var source = roles.RoleOf(from);
        var targetArea = roles.AreaOf(to);
        if (source == Role.CompositionRoot)
        {
            return EdgeKind.Allowed;
        }

        if (targetArea.Length > 0 && roles.AreaOf(from) == targetArea)
        {
            return EdgeKind.WithinArea;
        }

        // A platform child of a feature (Desktop.Windows, Chrome.MacOS) is still that feature's code to everyone
        // outside the feature, whatever role it has inside.
        var target = roles.RoleOf($"{roles.Root}.{targetArea}") == Role.Feature ? Role.Feature : roles.RoleOf(to);
        if (source == Role.Feature && target == Role.Feature)
        {
            return EdgeKind.CrossesFeature;
        }

        return target > source ? EdgeKind.PointsUp : EdgeKind.Allowed;
    }

    public static bool IsViolation(EdgeKind kind) => kind is EdgeKind.CrossesFeature or EdgeKind.PointsUp;
}
