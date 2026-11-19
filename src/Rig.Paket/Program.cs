using System.Text;
using Rig.Paket;

// Paket logs through Console.Out; the JSON owns standard output, so everything else goes to standard error.
using var output = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

var paths = (await Console.In.ReadToEndAsync().ConfigureAwait(false))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var readings = paths.Select(PaketFileReader.Read).ToList();
await output.WriteAsync(Encoding.UTF8.GetBytes(PaketJson.Write(readings))).ConfigureAwait(false);
return 0;
