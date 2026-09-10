using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FitForge.Api.Tests;

/// <summary>
/// Asserts <c>GET /health/live</c> against
/// <c>specs/001-solution-scaffold/contracts/health.md</c> §1. The BFF is written against
/// that contract, so a change here that nobody notices breaks the other repository.
/// </summary>
public class HealthLiveTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Returns_200_with_the_exact_contract_body()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // camelCase, per contract §3 — the property is "status", not "Status".
        Assert.True(body.TryGetProperty("status", out var status));
        Assert.Equal("live", status.GetString());

        // The contract declares exactly one field. An extra one is a silent contract
        // change, so assert the shape rather than only the value.
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task Performs_no_dependency_work()
    {
        // Liveness must answer with no database configured at all — which is the state
        // this test host runs in. If someone later gives it a dependency check, this
        // fails, and the reason is contract §1: liveness MUST NOT touch the database.
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
