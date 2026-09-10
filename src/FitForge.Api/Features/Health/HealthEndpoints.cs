using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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

        // GET /health/ready arrives in phase 2, with the dependency checks it reports on.

        return endpoints;
    }
}

/// <summary>Response body of <c>GET /health/live</c>. Serialized camelCase (contract §3).</summary>
internal sealed record LivenessResponse(string Status);
