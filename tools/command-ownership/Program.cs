using System.Text.Json;
using Rig.CommandOwnership;
using Rig.Core.CommandSurface;
using Rig.Core.Processes;

var help = args.Contains("--help", StringComparer.Ordinal);
if (args.Length < 2 || help)
{
    var usage = "Usage: dotnet run --project tools/command-ownership -- <repo> <built-rig.dll> [--inventory] [--base <git-ref>]";
    Console.Out.WriteLine(usage);
    var description = "Read-only ownership gate. --inventory prints inspected registrations for review; it never updates the ledger.";
    Console.Out.WriteLine(description);
    return help ? 0 : 2;
}

var root = Path.GetFullPath(args[0]);
var dll = Path.GetFullPath(args[1]);
var ledgerPath = Path.Join(root, "config", "command-ownership.json");
var proc = new SystemProc();
try
{
    var actual = Discovery.Read(root, dll, proc);
    if (args.Contains("--inventory", StringComparer.Ordinal))
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(actual, Discovery.Json));
        return 0;
    }

    var ledger = Discovery.Decode<OwnershipLedger>(File.ReadAllText(ledgerPath));
    OwnershipLedger? previous = null;
    var baseIndex = Array.IndexOf(args, "--base");
    if (baseIndex >= 0)
    {
        var baseRef = args[baseIndex + 1];
        // Bootstrap only when the ledger truly does not exist in the base tree. Other git failures refuse the check.
        var files = Discovery.Capture(proc, root, "git", "ls-tree", "--name-only", baseRef, "config/command-ownership.json");
        if (files.Trim().Length > 0)
        {
            previous = Discovery.Decode<OwnershipLedger>(Discovery.Capture(proc, root, "git", "show", $"{baseRef}:config/command-ownership.json"));
        }
    }

    var errors = CommandOwnership.Check(actual.Select(c => c.Id).ToArray(), ledger, previous).ToList();
    var expected = ledger.Commands.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    foreach (var row in actual)
    {
        if (expected.TryGetValue(row.Id, out var entry) && (entry.Capability != row.Capability || entry.Owner != row.Owner || entry.AliasTo != row.AliasTo || !entry.Platforms.Order(StringComparer.Ordinal).SequenceEqual(row.Platforms.Order(StringComparer.Ordinal))))
        {
            errors.Add($"Ownership does not match inspected registration: {row.Id}");
        }
    }

    errors.AddRange(ledger.RetiredFiles.Where(f => File.Exists(Path.Join(root, f))).Select(f => $"Retired source resurrected: {f}"));
    foreach (var error in errors)
    {
        Console.Error.WriteLine(error);
    }

    Console.Out.WriteLine($"Ownership: {actual.Length} inspected surfaces, {ledger.Baseline.Count} exact pre-existing overlaps, {errors.Count} violations.");
    return errors.Count == 0 ? 0 : 1;
}
catch (Exception error) when (error is InvalidOperationException or IOException or JsonException or ArgumentException or IndexOutOfRangeException)
{
    Console.Error.WriteLine($"Ownership discovery refused: {error.Message}");
    return 2;
}
