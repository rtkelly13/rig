using System.Text.Json;

namespace Rig.Core.GitHub;

/// <summary>Default JSON serialization options for GitHub API models.</summary>
public static class GitHubSerializerDefaults
{
    /// <summary>Web defaults with required constructor parameters and nullable annotations respected.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };
}
