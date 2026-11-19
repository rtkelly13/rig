using System.Text;
using System.Text.Json;
using Rig.Paket;

// Paket logs through Console.Out; the JSON owns standard output, so everything else goes to standard error.
using var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

if (args is ["--check"])
{
    // Parses a one-line dependencies file, so the check fails when Paket.Core and its dependencies did not publish with the sidecar.
    _ = Paket.DependenciesFile.FromSource(".", "source https://api.nuget.org/v3/index.json");
    return 0;
}

// The paths arrive as a JSON array and the answer leaves as JSON, both ASCII: neither depends on the console's code page.
var request = await Console.In.ReadToEndAsync().ConfigureAwait(false);
var paths = request.Trim().Length == 0 ? [] : JsonSerializer.Deserialize<string[]>(request) ?? [];
var readings = paths.Select(PaketFileReader.Read).ToList();
await output.WriteAsync(Encoding.UTF8.GetBytes(PaketJson.Write(readings))).ConfigureAwait(false);
return 0;
