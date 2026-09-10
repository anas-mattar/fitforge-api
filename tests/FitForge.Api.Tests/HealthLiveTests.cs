using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace FitForge.Api.Tests;

/// <summary>
/// Asserts <c>GET /health/live</c> against
/// <c>specs/001-solution-scaffold/contracts/health.md</c> §1. The BFF is written against
/// that contract, so a change here that nobody notices breaks the other repository.
/// </summary>
public class HealthLiveTests : IDisposable
{
    private readonly FitForgeApiFactory _factory = new();
    private readonly HttpClient _client;

    public HealthLiveTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

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
    public async Task Answers_even_when_no_dependency_is_usable()
    {
        // Liveness MUST NOT touch the database (contract §1). This host's database check
        // is failing, so if liveness ever starts consulting dependencies, this goes red.
        using var unhealthy = new FitForgeApiFactory
        {
            DatabaseStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
        };
        using var client = unhealthy.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
