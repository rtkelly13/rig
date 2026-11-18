#:property PublishAot=false
// Fails unless every job the aggregate `ci` job needs succeeded.
//
// Usage:
//   dotnet run scripts/require-jobs-succeeded.cs
//
// Reads NEEDS_JSON, the workflow's toJSON(needs): a mapping of job id to an object whose `result` is success,
// failure, cancelled or skipped. Only success and skipped pass. An empty or malformed NEEDS_JSON fails too.
using System.Text.Json;

#pragma warning disable CA1303 // CI log lines, not localizable UI text

JsonElement needs;
try
{
    needs = JsonDocument.Parse(Environment.GetEnvironmentVariable("NEEDS_JSON") ?? string.Empty).RootElement;
}
catch (JsonException)
{
    needs = default;
}

if (needs.ValueKind != JsonValueKind.Object || !needs.EnumerateObject().Any())
{
    Console.Out.WriteLine("::error::NEEDS_JSON is not a non-empty mapping of jobs.");
    return 1;
}

var failed = new List<string>();
foreach (var job in needs.EnumerateObject())
{
    string? result = null;
    if (job.Value.ValueKind == JsonValueKind.Object
        && job.Value.TryGetProperty("result", out var value)
        && value.ValueKind == JsonValueKind.String)
    {
        result = value.GetString();
    }

    if (result is not ("success" or "skipped"))
    {
        failed.Add($"{job.Name} ({result ?? "null"})");
    }
}

if (failed.Count > 0)
{
    failed.Sort(StringComparer.Ordinal);
    Console.Out.WriteLine($"::error::Required jobs did not succeed: {string.Join(", ", failed)}");
    return 1;
}

Console.Out.WriteLine("All checks passed.");
return 0;
