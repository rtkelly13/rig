using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.Jobs;
using Rig.Web.Mcp;
using OpsJson = Rig.Web.Ops.OpsJson;

namespace Rig.Web;

/// <summary>Request payload to start a job run.</summary>
/// <param name="Job">One job to run, due or not; null runs every job due on a tick.</param>
public sealed record JobRunRequest([property: JsonPropertyName("job")] string? Job = null);

/// <summary>
/// The jobs coordinator ops routes: registered jobs, run history, run output and triggering a background run.
/// </summary>
/// <remarks>
/// | route | method | answer |
/// |---|---|---|
/// | <c>/api/ops/jobs</c> | GET | <c>list[JobView]</c>: every registered job with its last outcome |
/// | <c>/api/ops/runs?job=&amp;limit=</c> | GET | <c>list[RunView]</c>: recent job runs, newest first |
/// | <c>/api/ops/runs/{job}/{runId}/output</c> | GET | <see cref="RunOutput"/>: what one run printed |
/// | <c>/api/ops/jobs/run</c> | POST | <see cref="RunStarted"/>: starts a detached background run |
/// </remarks>
public static class JobsRoutes
{
    private const string JobsPath = "/api/ops/jobs";
    private const string RunPath = "/api/ops/jobs/run";
    private const string RunsPath = "/api/ops/runs";
    private static readonly string[] AnyMethod = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>Maps the jobs routes onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoints go.</param>
    /// <param name="ops">The ops coordinator backing the routes.</param>
    public static void Map(IEndpointRouteBuilder routes, RigOps ops)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(JobsPath, Handler(context => OpsJson.Run(() =>
            Task.FromResult(JsonContent(context, ops.ListJobs())))));

        routes.MapPost(JobsPath, Handler(context => MethodNotSupported(context)));

        routes.MapGet(RunsPath, Handler(context => OpsJson.Run(() =>
        {
            var job = context.Request.Query.TryGetValue("job", out var jobVal) ? (string?)jobVal : null;
            var limit = ParseLimit(context);
            return Task.FromResult(JsonContent(context, ops.ListRuns(job, limit)));
        })));

        routes.MapPost(RunsPath, Handler(context => MethodNotSupported(context)));

        routes.MapGet($"{RunsPath}/{{job}}/{{runId}}/output", Handler(context => OpsJson.Run(() =>
        {
            var job = (string?)context.Request.RouteValues["job"] ?? string.Empty;
            var runId = (string?)context.Request.RouteValues["runId"] ?? string.Empty;
            try
            {
                return Task.FromResult(JsonContent(context, ops.RunOutput(job, runId)));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
            {
                return Task.FromResult(OpsJson.Error(StatusCodes.Status404NotFound, ex.Message));
            }
        })));

        routes.MapPost(RunPath, Handler(context => OpsJson.Run(async () =>
        {
            var body = await OpsJson.BodyAsync(context).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return OpsJson.Error(StatusCodes.Status400BadRequest, "A JSON request body is required");
            }

            JobRunRequest request;
            try
            {
                request = JsonSerializer.Deserialize<JobRunRequest>(body, OpsJson.Boundary)
                    ?? throw new JsonException("The request body must be a JSON object");
            }
            catch (JsonException ex)
            {
                return OpsJson.Error(StatusCodes.Status400BadRequest, $"Invalid JSON request body: {ex.Message}");
            }

            if (request.Job is { Length: 0 })
            {
                return OpsJson.Error(StatusCodes.Status400BadRequest, "`job` must name a registered job or be null");
            }

            try
            {
                var json = await ops.StartRunAsync(request.Job).ConfigureAwait(false);
                return JsonContent(context, json);
            }
            catch (KeyNotFoundException ex)
            {
                return OpsJson.Error(StatusCodes.Status404NotFound, ex.Message);
            }
        })));

        routes.MapGet(RunPath, Handler(_ => Task.FromResult(OpsJson.Error(
            StatusCodes.Status400BadRequest,
            "GET is not supported here; POST to start a run"))));

        routes.MapMethods($"{RunsPath}/{{job}}", AnyMethod, Handler(context =>
            OpsJson.Run(() => Task.FromResult(OpsJson.Error(
                StatusCodes.Status404NotFound,
                $"Nothing at {context.Request.Path.Value}")))));
    }

    private static int ParseLimit(HttpContext context)
    {
        if (!context.Request.Query.TryGetValue("limit", out var value) || string.IsNullOrEmpty(value))
        {
            return JobViews.DefaultLimit;
        }

        if (!int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException("`limit` must be an integer", nameof(context));
        }

        if (parsed is < 1 or > JobViews.MaxLimit)
        {
            throw new ArgumentException($"`limit` must be between 1 and {JobViews.MaxLimit}", nameof(context));
        }

        return parsed;
    }

    private static IResult JsonContent(HttpContext context, string json)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(json, "application/json; charset=utf-8");
    }

    private static Task<IResult> MethodNotSupported(HttpContext context) =>
        Task.FromResult(OpsJson.Error(StatusCodes.Status400BadRequest, $"{context.Request.Method} is not supported here"));

    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;
}
