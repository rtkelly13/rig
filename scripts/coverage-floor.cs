#:property PublishAot=false
// Coverage floor ratchet for rig's C# code: the floor never falls, and it follows coverage up to the target.
//
// Usage:
//   dotnet run scripts/coverage-floor.cs -- <coverage.json>... [--props Directory.Build.props] [--base-props PATH]
//
// The floor, target and slack are RigCoverageFloor, RigCoverageTarget and RigCoverageSlack in
// Directory.Build.props. Coverage is line and branch together, as the Python ratchet measures it, read from coverlet's
// json reports (--coverlet-output-format json), which keep each branch's identity so reports merge exactly.
// Rules, the same three as rig.repo_governance.coverage_floor:
//   - coverage may not drop below the floor;
//   - a pull request may not lower the floor (compared with the base branch's props file);
//   - once coverage is more than the slack above the floor, the floor must be raised, up to the target.
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

var positional = new List<string>();
var props = "Directory.Build.props";
string? baseProps = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--props": props = args[++i]; break;
        case "--base-props": baseProps = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count == 0)
{
    Console.Error.WriteLine("usage: coverage-floor.cs <coverage.json>... [--props PATH] [--base-props PATH]");
    return 2;
}

var (floor, target, slack) = ReadRatchet(props);
double? baseFloor = baseProps is not null && File.Exists(baseProps) ? ReadRatchet(baseProps).Floor : null;
var measured = Measure(positional);

var problems = new List<string>();
if (floor > target)
{
    problems.Add($"The floor {floor:g}% is above the target {target:g}%.");
}

if (baseFloor is { } before && floor < before)
{
    problems.Add($"The floor fell from {before:g}% to {floor:g}%. It only rises.");
}

if (measured < floor)
{
    problems.Add($"Coverage {measured:F2}% is below the floor {floor:g}%. Add tests, don't lower the floor.");
}
else if (floor < target && measured - floor > slack)
{
    var raised = Math.Min(target, Math.Floor(measured));
    problems.Add($"Coverage {measured:F2}% is more than {slack:g} points above the floor {floor:g}%. Raise RigCoverageFloor to {raised:g}.");
}

foreach (var problem in problems)
{
    Console.WriteLine($"::error::{problem}");
}

if (problems.Count == 0)
{
    Console.WriteLine($"Coverage {measured:F2}% holds the floor {floor:g}% (target {target:g}%).");
}

return problems.Count == 0 ? 0 : 1;

static (double Floor, double Target, double Slack) ReadRatchet(string path)
{
    var properties = XDocument.Load(path).Descendants();
    double Read(string name, double fallback)
    {
        var value = properties.FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        return value is null ? fallback : double.Parse(value, CultureInfo.InvariantCulture);
    }

    return (Read("RigCoverageFloor", 0), Read("RigCoverageTarget", 95), Read("RigCoverageSlack", 2));
}

static double Measure(IEnumerable<string> paths)
{
    // Several test projects can cover the same code. Merge on coverlet's own identities: a line is covered if
    // any report hit it, and a branch (one outcome of one jump: offset, target, path and ordinal) likewise, so
    // two projects that each take a different side of an `if` together cover both.
    var lines = new Dictionary<(string File, string Line), bool>();
    var branches = new Dictionary<(string File, int Line, int Offset, int EndOffset, int Path, int Ordinal), bool>();
    foreach (var path in paths)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var module in report.RootElement.EnumerateObject())
        {
            foreach (var document in module.Value.EnumerateObject())
            {
                var methods = document.Value.EnumerateObject().SelectMany(c => c.Value.EnumerateObject());
                foreach (var method in methods)
                {
                    foreach (var line in method.Value.GetProperty("Lines").EnumerateObject())
                    {
                        var key = (document.Name, line.Name);
                        lines[key] = lines.GetValueOrDefault(key) || line.Value.GetInt32() > 0;
                    }

                    foreach (var branch in method.Value.GetProperty("Branches").EnumerateArray())
                    {
                        int Get(string name) => branch.GetProperty(name).GetInt32();
                        var key = (document.Name, Get("Line"), Get("Offset"), Get("EndOffset"), Get("Path"), Get("Ordinal"));
                        branches[key] = branches.GetValueOrDefault(key) || Get("Hits") > 0;
                    }
                }
            }
        }
    }

    var covered = lines.Values.Count(hit => hit) + branches.Values.Count(hit => hit);
    var valid = lines.Count + branches.Count;
    return valid == 0 ? 100 : 100.0 * covered / valid;
}
