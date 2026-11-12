# Fluent GitHub API

`Rig.Core.GitHub` provides an expressive, functional API over `IGitHubApi` for interacting with GitHub's REST and GraphQL endpoints, strictly segregating data specifications, domain functions, and fluent wrappers.

## Architecture & Segregation

The design separates data models, execution functions, and ergonomic syntax into three distinct tiers:

1. **Pure Data Specifications (No Behaviour)**:
   - `RestRequestSpec`: Pure record defining HTTP method, route, body, headers, and query parameters.
   - `GraphQLRequestSpec`: Pure record defining query document, variables, operation name, and description.

2. **Standalone Domain Functions (`GitHubFunctions`)**:
   - Pure and static functions implementing all execution, serialization, URL encoding, HTTP 404 detection, and error parsing.
   - Can be invoked directly without using fluent builders:
     ```csharp
     var widget = await GitHubFunctions.ReadJsonAsync<Widget>(api, spec, cancellationToken: ct);
     ```

3. **Thin Fluent Wrapper Layer**:
   - `GitHubRestRequest` and `GitHubGraphQLRequest` are thin, immutable builders over `RestRequestSpec` and `GraphQLRequestSpec`.
   - Chaining methods (`WithQuery`, `WithHeader`, `WithVariable`) return updated instances.
   - Every execution method (`ReadJsonAsync`, `ExecuteAsync`, etc.) is a one-line delegation to `GitHubFunctions`.
   - `GitHubRepoClient`: Thin wrapper created via `api.ForRepo(repo)` pre-populating repo routes and GraphQL variables.

---

## Usage Examples

### Fluent Wrapper

```csharp
using Rig.Core.GitHub;

// GET with query parameters and typed JSON deserialization
var labels = await api.ForRepo(repo)
    .Get("labels")
    .WithQuery("per_page", 100)
    .ReadJsonAsync<IReadOnlyList<RepoLabel>>(cancellationToken: ct);

// Optional read: returns null on HTTP 404
var rules = await api.ForRepo(repo)
    .Get($"rules/branches/{branch}")
    .WithQuery("per_page", 100)
    .ReadOptionalJsonAsync<List<WireRule>>(cancellationToken: ct);

// GraphQL query with typed response
var data = await api.ForRepo(repo)
    .GraphQL(Query)
    .Describing($"landing state of {repo}#{number}")
    .WithVariable("number", number)
    .ExecuteAsync<WireData>(cancellationToken: ct);
```

### Direct Functions

```csharp
using Rig.Core.GitHub;

// Executing directly via GitHubFunctions without fluent wrappers
var spec = new RestRequestSpec("GET", $"repos/{repo}/rules/branches/{branch}", QueryParameters: new Dictionary<string, string> { ["per_page"] = "100" });
var rules = await GitHubFunctions.ReadOptionalJsonAsync<List<WireRule>>(api, spec, cancellationToken: ct);
```

---

## Design Principles

1. **Segregation of Data and Functions**: Data classes contain zero execution or domain logic. Functions are pure and static.
2. **Thin Fluent Wrapper**: Fluent builders are lightweight adapters that accumulate specification data and delegate execution.
3. **Immutable Chaining**: Each configuration method returns a new record instance using `this with { ... }`.
4. **Zero Mutable Additions**: Conforms to Rig's functional quality gates (no mutable collections or banned `List.AddRange` calls).
5. **Seam Preservation**: Underlying execution flows through `IGitHubApi.CallAsync(ApiRequest)`, maintaining 100% test fake compatibility.
