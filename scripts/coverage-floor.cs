#:property PublishAot=false
// Coverage floor ratchet for rig's C# code: the floor never falls, and it follows coverage up to the target.
//
// Usage:
//   dotnet run scripts/coverage-floor.cs -- <cobertura.xml>... [--props Directory.Build.props] [--base-props PATH]
//
// The floor, target and slack are RigCoverageFloor, RigCoverageTarget and RigCoverageSlack in
// Directory.Build.props. Coverage is line and branch together, as the Python ratchet measures it.
// Rules, the same three as rig.repo_governance.coverage_floor:
//   - coverage may not drop below the floor;
//   - a pull request may not lower the floor (compared with the base branch's props file);
//   - once coverage is more than the slack above the floor, the floor must be raised, up to the target.
using System.Globalization;
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
    Console.Error.WriteLine("usage: coverage-floor.cs <cobertura.xml>... [--props PATH] [--base-props PATH]");
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
    // Several test projects can cover the same source line. Merge by (file, line): a line is covered
    // if any report hit it, and a branch line counts the most conditions any report covered.
    var lines = new Dictionary<(string File, string Line), (bool Hit, int Covered, int Total)>();
    foreach (var path in paths)
    {
        foreach (var @class in XDocument.Load(path).Descendants("class"))
        {
            var file = @class.Attribute("filename")?.Value ?? "";
            foreach (var line in @class.Elements("lines").Elements("line"))
            {
                var key = (file, line.Attribute("number")?.Value ?? "");
                var hit = int.Parse(line.Attribute("hits")?.Value ?? "0", CultureInfo.InvariantCulture) > 0;
                var (covered, total) = Conditions(line.Attribute("condition-coverage")?.Value);
                lines[key] = lines.TryGetValue(key, out var seen)
                    ? (seen.Hit || hit, Math.Max(seen.Covered, covered), Math.Max(seen.Total, total))
                    : (hit, covered, total);
            }
        }
    }

    var coveredUnits = lines.Values.Sum(l => (l.Hit ? 1 : 0) + l.Covered);
    var validUnits = lines.Values.Sum(l => 1 + l.Total);
    return validUnits == 0 ? 100 : 100.0 * coveredUnits / validUnits;
}

// "50% (1/2)" -> (1, 2); a line with no branches -> (0, 0).
static (int Covered, int Total) Conditions(string? coverage)
{
    var open = coverage?.IndexOf('(', StringComparison.Ordinal) ?? -1;
    if (coverage is null || open < 0)
    {
        return (0, 0);
    }

    var parts = coverage[(open + 1)..].TrimEnd(')').Split('/');
    return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
}
