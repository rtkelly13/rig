using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Packages;

/// <summary>The job that runs <c>NuGet/login</c>, read as far as a trusted publishing policy cares.</summary>
/// <param name="Name">The job id.</param>
/// <param name="IdTokenWrite">Whether it holds <c>id-token: write</c>, its own or inherited from the workflow.</param>
/// <param name="UserInput">The <c>user:</c> input as written.</param>
/// <param name="User">The profile name it resolves to: the literal itself, or a <c>|| 'literal'</c> fallback.</param>
/// <param name="Environment">The environment it runs in.</param>
/// <param name="PushFound">A <c>nuget push</c> step follows the login in the same job.</param>
/// <param name="SkipDuplicate">Every such push passes <c>--skip-duplicate</c>.</param>
/// <param name="PushSecrets"><c>secrets.NUGET_*</c> the push reads: a long-lived key next to the OIDC one.</param>
public sealed record LoginJob(
    string Name, bool IdTokenWrite, string? UserInput, string? User, string? Environment, bool PushFound, bool SkipDuplicate, IReadOnlyList<string> PushSecrets);

/// <summary>A publish workflow's NuGet login jobs, and the reusable workflows it calls (a policy must name the called file).</summary>
/// <param name="LoginJobs">The jobs that run NuGet/login.</param>
/// <param name="Called">The file names of the reusable workflows its jobs call.</param>
public sealed record PublishWorkflow(IReadOnlyList<LoginJob> LoginJobs, IReadOnlyList<string> Called);

/// <summary>One NuGet trusted publishing policy's fields, as the form at <see cref="NuGetGovernance.TrustUrl"/> asks for them.</summary>
/// <param name="PolicyOwner">The nuget.org user or organisation that owns it.</param>
/// <param name="RepositoryOwner">The GitHub repository's owner.</param>
/// <param name="Repository">The GitHub repository's name.</param>
/// <param name="Workflow">The workflow file.</param>
/// <param name="Environment">The environment, if the policy sets one.</param>
/// <param name="Scopes">Its scopes.</param>
/// <param name="PackageIds">The packages it covers.</param>
public sealed record PolicyChecklist(
    string? PolicyOwner, string RepositoryOwner, string Repository, string Workflow, string? Environment, IReadOnlyList<string> Scopes, IReadOnlyList<string> PackageIds)
{
    /// <summary>A glob covering <see cref="PackageIds"/>.</summary>
    public string PackageGlob => NuGetGovernance.PackageGlob(PackageIds);

    /// <summary>The fields, one per line.</summary>
    public string Render() => string.Join(
        '\n',
        $"policy owner (nuget.org user or organisation): {PolicyOwner ?? "? (declare nuget_owner)"}",
        $"repository owner: {RepositoryOwner}",
        $"repository: {Repository}",
        $"workflow file: {NuGetGovernance.FileName(Workflow)}",
        $"environment: {Environment ?? "(none)"}",
        $"scopes: {(Scopes.Count == 0 ? "? (declare scopes)" : string.Join(", ", Scopes))}",
        $"packages: {PackageGlob}",
        $"at: {NuGetGovernance.TrustUrl}",
        $"note: {NuGetGovernance.PolicyLifetimeNote}");
}

/// <summary>
/// NuGet trusted publishing: what rig can verify around a policy it cannot read. nuget.org has no API for policies;
/// they are created, listed and deleted only on the website. A policy matches a GitHub OIDC token on the repository
/// owner and name (case-insensitively), the workflow file name (for a reusable workflow, the called file) and the
/// environment if it sets one. A new policy is temporary for 7 days; its first publish locks the owner and repository
/// IDs, so after a transfer the old policy never matches again. This half reads the publish workflow into facts and
/// renders the checklist for the website; <see cref="PackageAudit"/> turns the facts into findings.
/// </summary>
public static partial class NuGetGovernance
{
    /// <summary>Where policies are managed.</summary>
    public const string TrustUrl = "https://www.nuget.org/account/trustedpublishing";

    /// <summary>The lifecycle note printed with every checklist.</summary>
    public const string PolicyLifetimeNote =
        "temporary until first publish, 7 days; after a transfer add the new policy, publish, then delete the old one";

    /// <summary>The login jobs of the workflow in <paramref name="text"/>; null when it is not a workflow.</summary>
    public static PublishWorkflow? ReadPublishWorkflow(string text)
    {
        PyValue? loaded;
        try
        {
            loaded = PyYaml.Load(text);
        }
        catch (PyYamlException)
        {
            return null;
        }

        if (loaded is not PyDict workflow || Get(workflow, "jobs") is not PyDict jobs)
        {
            return null;
        }

        var inherited = IdTokenWrite(Get(workflow, "permissions"));
        var found = new List<LoginJob>();
        var called = new List<string>();
        foreach (var (key, value) in jobs.Entries)
        {
            if (value is not PyDict job)
            {
                continue;
            }

            if (Str(Get(job, "uses")) is { Length: > 0 } uses)
            {
                called.Add(FileName(uses.Split('@', 2)[0]));
            }

            if (Login(KeyText(key), job, inherited) is { } login)
            {
                found.Add(login);
            }
        }

        return new(found, called);
    }

    /// <summary>
    /// A glob covering every id: their shared prefix plus <c>*</c>, else the ids themselves. A prefix ending mid-word is
    /// cut back to its last <c>.</c>, so <c>Foo.Bar</c> and <c>Foo.Baz</c> give <c>Foo.*</c>; one that is a whole id is
    /// kept, so <c>Parquet.SourceGenerator</c> and <c>Parquet.SourceGenerator.Legacy</c> give <c>Parquet.SourceGenerator*</c>.
    /// </summary>
    public static string PackageGlob(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 1)
        {
            return ids[0];
        }

        var lowered = ids.Select(id => id.ToLowerInvariant()).ToList();
        var prefix = lowered.Count == 0 ? string.Empty : lowered.Aggregate(CommonPrefix);
        var whole = ids.FirstOrDefault(id => string.Equals(id, prefix, StringComparison.OrdinalIgnoreCase));
        prefix = whole ?? prefix[..(prefix.LastIndexOf('.') + 1)];
        return prefix.TrimEnd('.').Length < 4 ? string.Join(", ", ids) : ids[0][..prefix.Length] + "*";
    }

    /// <summary>The last segment of a POSIX path.</summary>
    internal static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static string CommonPrefix(string left, string right)
    {
        var length = 0;
        while (length < Math.Min(left.Length, right.Length) && left[length] == right[length])
        {
            length++;
        }

        return left[..length];
    }

    private static LoginJob? Login(string name, PyDict job, bool? inherited)
    {
        var steps = Get(job, "steps") is PyList list ? list.Items.OfType<PyDict>().ToList() : [];
        var at = steps.FindIndex(step => (Str(Get(step, "uses")) ?? string.Empty).StartsWith("nuget/login", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            return null;
        }

        var userInput = Get(steps[at], "with") is PyDict inputs ? Str(Get(inputs, "user")) : null;
        var pushes = steps.Skip(at + 1).Where(step => PushPattern().IsMatch(Str(Get(step, "run")) ?? string.Empty)).ToList();
        var texts = pushes.Select(step => Str(Get(step, "run")) ?? string.Empty)
            .Concat(pushes.SelectMany(step => Get(step, "env") is PyDict env ? env.Entries.Select(e => Str(e.Value) ?? string.Empty) : []));
        var secrets = texts.SelectMany(text => NuGetSecretPattern().Matches(text).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var own = IdTokenWrite(Get(job, "permissions"));
        return new(
            name,
            (own ?? inherited) ?? false,
            userInput,
            ResolveUser(userInput),
            Get(job, "environment") switch
            {
                PyDict environment => Str(Get(environment, "name")),
                var other => Str(other),
            },
            pushes.Count > 0,
            pushes.Count > 0 && pushes.All(step => SkipDuplicatePattern().IsMatch(Str(Get(step, "run")) ?? string.Empty)),
            secrets);
    }

    // Null when nothing is declared, so a job can fall back to the workflow's permissions.
    private static bool? IdTokenWrite(PyValue? permissions) => permissions switch
    {
        PyStr text => text.Text == "write-all",
        PyDict map => Str(Get(map, "id-token")) == "write",
        _ => null,
    };

    // `${{ secrets.NUGET_USER || 'rtk_13' }}`: the quoted literal after the last `||` is what runs when the secret is
    // unset, which is the value the policy owner has to match.
    private static string? ResolveUser(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var fallback = FallbackPattern().Match(value);
        if (fallback.Success)
        {
            return fallback.Groups[1].Value;
        }

        return value.Contains("${{", StringComparison.Ordinal) ? null : value.Trim();
    }

    private static PyValue? Get(PyDict map, string key) =>
        map.Entries.LastOrDefault(entry => entry.Key is PyStr text && text.Text == key).Value;

    private static string? Str(PyValue? value) => value is PyStr text ? text.Text : null;

    // A job id as GitHub reads it: Python's str() of the key.
    private static string KeyText(PyValue key) => key switch
    {
        PyStr text => text.Text,
        PyScalar scalar => scalar.Rendered,
        PyNone => "None",
        _ => string.Empty,
    };

    [GeneratedRegex("""\|\|\s*['"]([^'"]+)['"]\s*}}\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex FallbackPattern();

    [GeneratedRegex(@"secrets\.(NUGET_API_KEY|NUGET_TOKEN|NUGET_KEY\w*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex NuGetSecretPattern();

    [GeneratedRegex(@"\bnuget\s+push\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PushPattern();

    [GeneratedRegex(@"--skip-duplicate\b|-SkipDuplicate\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SkipDuplicatePattern();
}
