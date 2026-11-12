using System.Security.Cryptography;
using System.Text;

namespace Rig.Core.Connectivity;

/// <summary>Condenses strongly connected repos into cycle nodes, so the graph drawn is a DAG and every original edge stays explainable.</summary>
public static class Condensation
{
    /// <summary>
    /// The components of <paramref name="projects"/> under <paramref name="dependencies"/> (Tarjan's algorithm, nodes and
    /// their targets visited in ordinal order so the answer is deterministic) and the distinct edges between them.
    /// </summary>
    public static Dag Condense(IReadOnlyList<GraphProject> projects, IReadOnlyList<Dependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(dependencies);
        var adjacency = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            adjacency[project.Id] = new SortedSet<string>(StringComparer.Ordinal);
        }

        foreach (var edge in dependencies.Where(edge => adjacency.ContainsKey(edge.Source) && adjacency.ContainsKey(edge.Target)))
        {
            adjacency[edge.Source].Add(edge.Target);
        }

        var tarjan = new Tarjan(adjacency);
        foreach (var node in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            if (!tarjan.Seen(node))
            {
                tarjan.Visit(node);
            }
        }

        var components = tarjan.Components.OrderBy(component => component.Id, StringComparer.Ordinal).ToList();
        var owners = components.SelectMany(component => component.Members.Select(member => (member, component.Id)))
            .ToDictionary(pair => pair.member, pair => pair.Id, StringComparer.Ordinal);
        var edges = dependencies
            .Where(edge => owners.ContainsKey(edge.Source) && owners.ContainsKey(edge.Target))
            .Select(edge => (Source: owners[edge.Source], Target: owners[edge.Target]))
            .Where(pair => pair.Source != pair.Target)
            .Distinct()
            .OrderBy(pair => pair.Source, StringComparer.Ordinal)
            .ThenBy(pair => pair.Target, StringComparer.Ordinal)
            .Select(pair => new ComponentEdge(pair.Source, pair.Target))
            .ToList();
        return new Dag(components, edges);
    }

    /// <summary>A cycle's id: <c>cycle:</c> and the first 12 hex digits of the SHA-256 of its sorted members joined by <c>|</c>.</summary>
    public static string CycleId(IEnumerable<string> members) =>
        "cycle:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', members))))[..12];

    private sealed class Tarjan(Dictionary<string, SortedSet<string>> adjacency)
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _low = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly Stack<string> _pending = new();

        public List<Component> Components { get; } = [];

        public bool Seen(string node) => _index.ContainsKey(node);

        public void Visit(string node)
        {
            _index[node] = _index.Count;
            _low[node] = _index[node];
            _pending.Push(node);
            _active.Add(node);
            foreach (var target in adjacency[node])
            {
                if (!Seen(target))
                {
                    Visit(target);
                    _low[node] = Math.Min(_low[node], _low[target]);
                }
                else if (_active.Contains(target))
                {
                    _low[node] = Math.Min(_low[node], _index[target]);
                }
            }

            if (_low[node] != _index[node])
            {
                return;
            }

            var members = new List<string>();
            while (_pending.TryPop(out var member))
            {
                _active.Remove(member);
                members.Add(member);
                if (member == node)
                {
                    break;
                }
            }

            members.Sort(StringComparer.Ordinal);
            var cyclic = members.Count > 1 || adjacency[node].Contains(node);
            Components.Add(new Component(cyclic ? CycleId(members) : node, members, cyclic));
        }
    }
}
