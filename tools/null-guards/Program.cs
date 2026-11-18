using Rig.NullGuards;

var help = args.Contains("--help", StringComparer.Ordinal);
if (args.Length < 1 || help)
{
    Console.Out.WriteLine("Usage: dotnet run --project tools/null-guards -- <repo> [--dry-run]");
    Console.Out.WriteLine("SHORT-TERM (#318). Build src/ first (dotnet build src/Rig.Cli): types resolve against each project's bin/Debug/net10.0 output.");
    Console.Out.WriteLine("Removes ArgumentNullException.ThrowIfNull(p) where p is a non-nullable reference-type parameter in a nullable-enabled context.");
    return help ? 0 : 2;
}

var root = Path.GetFullPath(args[0]);
var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
var totals = new Tally();
foreach (var project in Directory.EnumerateFiles(Path.Join(root, "src"), "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var tally = Codemod.Run(project, dryRun);
    Console.Out.WriteLine($"{Path.GetFileNameWithoutExtension(project)}: {tally.Summary()}");
    totals.Add(tally);
}

Console.Out.WriteLine($"total: {totals.Summary()}{(dryRun ? " (dry run, nothing written)" : string.Empty)}");
return 0;
