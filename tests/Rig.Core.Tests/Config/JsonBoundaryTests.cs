using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Config;
using Rig.Core.Connectivity;
using Rig.Core.GitHub;
using Rig.Core.ReviewQueue;

namespace Rig.Core.Tests.Config;

/// <summary>
/// Nullable annotations are the contract, so everything rig reads from outside must enforce them at the JSON boundary (#318).
/// A new context or options object that reads input and skips the flags fails here.
/// </summary>
public sealed class JsonBoundaryTests
{
    [Fact]
    public void Every_JsonSerializerContext_respects_nullable_annotations()
    {
        var contexts = typeof(EstateConfigJsonContext).Assembly.GetTypes().Where(t => typeof(JsonSerializerContext).IsAssignableFrom(t)).ToList();

        Assert.NotEmpty(contexts);
        foreach (var context in contexts)
        {
            var options = context.GetCustomAttribute<JsonSourceGenerationOptionsAttribute>();
            Assert.True(options?.RespectNullableAnnotations, $"{context.Name} must set RespectNullableAnnotations = true");
        }
    }

    [Fact]
    public void Every_options_object_that_reads_input_enforces_the_contract()
    {
        var readers = new Dictionary<string, JsonSerializerOptions>(StringComparer.Ordinal)
        {
            ["EstateGraph.JsonOptions"] = EstateGraph.JsonOptions,
            ["GitHubSerializerDefaults.Options"] = GitHubSerializerDefaults.Options,
            ["QueueJson.Options"] = QueueJson.Options,
            ["EstateConfigJsonContext"] = EstateConfigJsonContext.Default.Options,
        };

        foreach (var (name, options) in readers)
        {
            Assert.True(options.RespectNullableAnnotations, $"{name} must set RespectNullableAnnotations = true");
        }

        foreach (var name in readers.Keys.Where(k => k != "EstateConfigJsonContext"))
        {
            Assert.True(readers[name].RespectRequiredConstructorParameters, $"{name} must set RespectRequiredConstructorParameters = true");
        }
    }
}
