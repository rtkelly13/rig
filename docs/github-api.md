# Fluent GitHub API

`Rig.Core.GitHub` provides an expressive, immutable fluent API over `IGitHubApi` for interacting with GitHub's REST and GraphQL endpoints.

## Core Concepts

The API is built on top of the existing `IGitHubApi` seam, ensuring that all existing test fakes (`FakeGitHub`, `FakeGraphQL`, `FakeLanding`) remain 100% compatible while callers enjoy a rich, functional builder experience.

### Key Types

- **`GitHubRestRequest`**: Immutable record for building REST requests (`GET`, `POST`, `PATCH`, `PUT`, `DELETE`). Supports query parameters, headers, and request payloads.
- **`GitHubGraphQLRequest`**: Immutable record for building GraphQL queries and mutations. Supports variables, operation names, and automatic error extraction.
- **`GitHubRepoClient`**: Repository-scoped client created via `api.ForRepo(repo)`. Pre-populates repository routes for REST and `owner`/`name` variables for GraphQL.
- **`GitHubApiException`**: Unified exception thrown on HTTP failures, non-zero process exits, or GraphQL errors.

---

## Usage Examples

### REST Operations

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

// POST with JSON body
await api.ForRepo(repo)
    .Post("labels")
    .WithJsonBody(new { name = "bug", color = "f29513", description = "Something isn't working" })
    .EnsureSuccessAsync(cancellationToken: ct);
```

### GraphQL Operations

```csharp
using Rig.Core.GitHub;

const string Query = """
    query LandingState($owner: String!, $name: String!, $number: Int!) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $number) {
          state
          mergeable
        }
      }
    }
    """;

// Executing a GraphQL query with typed response and error extraction
var data = await api.ForRepo(repo)
    .GraphQL(Query)
    .Describing($"landing state of {repo}#{number}")
    .WithVariable("number", number)
    .ExecuteAsync<WireData>(cancellationToken: ct);
```

---

## Design Principles

1. **Immutable Chaining**: Each configuration method (`WithQuery`, `WithHeader`, `WithVariable`) returns a new record instance using `this with { ... }`.
2. **Zero Mutable Additions**: Requests and headers avoid mutable collections or banned `List.AddRange` APIs, conforming to Rig's functional quality gates.
3. **Seam Preservation**: Underlying execution flows through `IGitHubApi.CallAsync(ApiRequest)`, allowing unit and integration tests to drive interactions with lightweight in-memory fakes.
