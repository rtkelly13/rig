using Rig.LocReport;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.Out.WriteLine("Usage: dotnet run --project tools/loc-report -- [<repo>] [--markdown]");
    Console.Out.WriteLine("Report-only: C# lines per area under src/ and Python carry-over counts. Always exits 0.");
    Console.Out.WriteLine("Short-term: to be replaced by Diffra's line counter and deleted (#320).");
    return 0;
}

var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".");
Console.Out.Write(Counter.Build(root).Render(args.Contains("--markdown", StringComparer.Ordinal)));
return 0;
