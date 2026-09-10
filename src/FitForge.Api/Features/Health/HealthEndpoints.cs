using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FitForge.Api.Features.Health;

/// <summary>
/// Health endpoints, per <c>specs/001-solution-scaffold/contracts/health.md</c> §1.
/// </summary>
/// <remarks>
/// One folder per slice (ADR-001 §4.2): the endpoint mapping, its response contracts and
/// its handler live together, so adding a feature means adding a folder.
/// </remarks>
internal static class HealthEndpoints
{
    internal static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness answers one question — is this process able to serve HTTP? It performs
        // no dependency work and MUST NOT touch the database (contract §1). There is no
        // failure response: a process that cannot answer is, by definition, not live, and
        // the caller observes that as a transport failure.
        endpoints.MapGet("/health/live", () => Results.Ok(new LivenessResponse("live")))
            .WithName("HealthLive")
            .WithTags("Health");

        // Readiness answers: can this process serve real requests right now? 200 when
        // every dependency is usable, 503 when at least one is not (contract §1).
        endpoints.MapGet("/health/ready", async (HealthCheckService healthChecks, CancellationToken cancellationToken) =>
        {
            var report = await healthChecks.CheckHealthAsync(cancellationToken);

            // One rule, applied at both levels: anything short of Healthy is not ready.
            //
            // Until phase 8 the document tested `== Unhealthy` while each check tested
            // `== Healthy`, so a Degraded dependency landed on opposite sides — HTTP 200
            // and "status":"ready", containing "status":"failed". The BFF branches on the
            // status code, so that reached the browser as a ready API with a dependency
            // down. Unreachable with one check; reachable the moment there are two.
            var ready = report.Status == HealthStatus.Healthy;

            var response = new ReadinessResponse(
                Status: ready ? "ready" : "degraded",
                Checks: report.Entries
                    .Select(entry => new ReadinessCheck(
                        Name: entry.Key,
                        Status: entry.Value.Status == HealthStatus.Healthy ? "ready" : "failed",
                        DurationMs: (long)entry.Value.Duration.TotalMilliseconds))
                    .OrderBy(check => check.Name, System.StringComparer.Ordinal)
                    .ToArray());

            // Deliberately nothing else. The check's own Description and Exception carry
            // server names, connection strings and provider messages, and this endpoint
            // is reachable by anything that can reach the API (contract §1).
            return ready
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
            .WithName("HealthReady")
            .WithTags("Health");

        return endpoints;
    }
}

/// <summary>Response body of <c>GET /health/live</c>. Serialized camelCase (contract §3).</summary>
internal sealed record LivenessResponse(string Status);

/// <summary>Response body of <c>GET /health/ready</c> (contract §1).</summary>
internal sealed record ReadinessResponse(string Status, IReadOnlyList<ReadinessCheck> Checks);

/// <summary>
/// One dependency's result. <c>Name</c> is a stable machine identifier, lower-case,
/// never a display string (contract §1). <c>DurationMs</c> is integer milliseconds,
/// per the suffix convention in contract §3.
/// </summary>
internal sealed record ReadinessCheck(string Name, string Status, long DurationMs);
