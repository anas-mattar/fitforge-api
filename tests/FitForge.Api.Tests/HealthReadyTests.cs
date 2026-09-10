using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FitForge.Api.Tests;

/// <summary>
/// Asserts <c>GET /health/ready</c> against
/// <c>specs/001-solution-scaffold/contracts/health.md</c> §1. The BFF branches on these
/// two status codes, so a change here silently changes the other repository's behaviour.
/// </summary>
public class HealthReadyTests
{
    [Fact]
    public async Task Every_dependency_usable_returns_200_and_the_contract_shape()
    {
        using var factory = new FitForgeApiFactory { DatabaseStatus = HealthStatus.Healthy };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());

        var check = Assert.Single(body.GetProperty("checks").EnumerateArray());
        Assert.Equal("database", check.GetProperty("name").GetString());
        Assert.Equal("ready", check.GetProperty("status").GetString());
        Assert.True(check.GetProperty("durationMs").TryGetInt64(out _), "durationMs must be an integer count of milliseconds (contract §3).");
    }

    [Fact]
    public async Task A_failed_dependency_returns_503_and_reports_degraded()
    {
        using var factory = new FitForgeApiFactory { DatabaseStatus = HealthStatus.Unhealthy };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", body.GetProperty("status").GetString());

        var check = Assert.Single(body.GetProperty("checks").EnumerateArray());
        Assert.Equal("failed", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_degraded_dependency_is_not_reported_as_ready()
    {
        // The framework has three states, the contract has two. Anything short of
        // Healthy must read "failed" — reporting a Degraded dependency as "ready" would
        // tell the BFF everything is fine while a query is timing out.
        using var factory = new FitForgeApiFactory { DatabaseStatus = HealthStatus.Degraded };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var check = Assert.Single(body.GetProperty("checks").EnumerateArray());
        Assert.Equal("failed", check.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_failure_discloses_no_connection_detail()
    {
        // A real SQL Server failure puts the server name, the database name and often the
        // login in the check's Description and Exception. This endpoint is reachable by
        // anything that can reach the API (contract §1), so none of that may pass through.
        const string Secret = "Login failed for user 'sa' on server db-prod-01.internal";

        using var factory = new FitForgeApiFactory
        {
            DatabaseStatus = HealthStatus.Unhealthy,
            DatabaseFailureDetail = Secret,
        };
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var raw = await response.Content.ReadAsStringAsync();

        // Asserted against the whole document, not one property: a leak shows up in
        // whichever field nobody thought to check.
        Assert.DoesNotContain(Secret, raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("db-prod-01", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", raw, System.StringComparison.Ordinal);
    }
}
