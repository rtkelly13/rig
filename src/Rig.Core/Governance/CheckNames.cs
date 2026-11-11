using Rig.Core.Vault;

namespace Rig.Core.Governance;

/// <summary>
/// The check names a repo's workflows report on a pull request, read off the YAML on disk. A required status check is a
/// contract between the workflows and the ruleset, and nothing in GitHub connects the two halves; see
/// docs/repo-governance/required-checks.md. Ported from <c>rig.repo_governance.check_names</c>.
/// </summary>
public static class CheckNames
{
    /// <summary>
    /// <c>{check name: "workflow file :: job id"}</c> for every job in a workflow triggered by <c>pull_request</c>: a
    /// job's <c>name:</c>, falling back to its id. A workflow's <c>env.STATUS_CONTEXT</c> counts too, as
    /// <c>"workflow file :: status"</c>: a commit status it posts. A file that does not parse is skipped, not fatal.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DiscoverPrCheckNames(string repoPath)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var workflows = Path.Join(repoPath, ".github", "workflows");
        if (!Directory.Exists(workflows))
        {
            return found;
        }

        var files = Directory.EnumerateFiles(workflows)
            .Where(file => IsWorkflowName(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (Load(file) is not { } workflow || Lookup(workflow, "jobs") is not PyDict jobs || !Triggers(workflow).Contains("pull_request"))
            {
                continue;
            }

            var fileName = Path.GetFileName(file);
            foreach (var (key, job) in jobs.Entries)
            {
                var jobId = KeyText(key);
                var name = job is PyDict body && Lookup(body, "name") is PyStr { Text: var named } ? named : jobId;
                _ = found.TryAdd(name, $"{fileName} :: {jobId}");
            }

            if (Lookup(workflow, "env") is PyDict env && Lookup(env, "STATUS_CONTEXT") is PyStr { Text: { Length: > 0 } status })
            {
                _ = found.TryAdd(status, $"{fileName} :: status");
            }
        }

        return found;
    }

    // `*.y*ml`, as pathlib's glob matches it.
    private static bool IsWorkflowName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        var extension = name[(dot + 1)..];
        return extension.StartsWith('y') && extension.EndsWith("ml", StringComparison.Ordinal) && extension.Length >= 3;
    }

    // The workflow's top-level mapping with string keys only, PyYAML's `on: → True` folded back; null when unreadable.
    private static PyDict? Load(string file)
    {
        PyValue? loaded;
        try
        {
            loaded = PyYaml.Load(File.ReadAllText(file));
        }
        catch (Exception e) when (e is PyYamlException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }

        if (loaded is not PyDict document)
        {
            return null;
        }

        var hasOn = document.Entries.Any(e => e.Key is PyStr { Text: "on" });
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var entry in document.Entries)
        {
            if (entry.Key is PyStr)
            {
                entries.Add(entry);
            }
            else if (entry.Key is PyScalar { Rendered: "True" } && !hasOn)
            {
                entries.Add(new(new PyStr("on"), entry.Value));
            }
        }

        return new PyDict(entries);
    }

    // The triggers a workflow's `on:` names: a mapping's keys, a list's items or a lone scalar.
    private static HashSet<string> Triggers(PyDict workflow) => Lookup(workflow, "on") switch
    {
        PyDict events => [.. events.Entries.Select(e => KeyText(e.Key))],
        PyList events => [.. events.Items.Select(KeyText)],
        PyNone or null => [],
        var single => [KeyText(single)],
    };

    // The last value under string key `key`, as a Python dict holds it.
    private static PyValue? Lookup(PyDict map, string key) =>
        map.Entries.LastOrDefault(e => e.Key is PyStr { Text: var text } && text == key).Value;

    private static string KeyText(PyValue value) => value switch
    {
        PyStr text => text.Text,
        PyScalar scalar => scalar.Rendered,
        PyNone => "None",
        _ => value.ToString() ?? string.Empty,
    };
}
