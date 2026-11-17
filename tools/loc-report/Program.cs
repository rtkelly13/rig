using Rig.LocReport;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.Out.WriteLine("Usage: dotnet run --project tools/loc-report -- [<repo>] [--markdown]");
    Console.Out.WriteLine("Report-only: C# lines per area under src/ and Python carry-over counts. Always exits 0.");
    return 0;
}

var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".");
Console.Out.Write(Counter.Build(root).Render(args.Contains("--markdown", StringComparer.Ordinal)));
return 0;
