using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FitForge.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Tests;

/// <summary>
/// Readiness must answer within three seconds even when the database is unreachable
/// (<c>specs/001-solution-scaffold/contracts/health.md</c> §1).
/// </summary>
/// <remarks>
/// This exists because of an observed defect, not a hypothetical one. An unbounded
/// <c>AddDbContextCheck</c> against an unreachable SQL Server took ~15 seconds on a cold
/// attempt; the BFF's 10-second timeout fired first, and the API's honest "degraded"
/// arrived at the browser as "unreachable". The two words send whoever is debugging to
/// different processes, so collapsing them is the whole cost.
/// </remarks>
public class HealthReadyTimeoutTests
{
    [Fact]
    public void The_database_check_is_bounded_below_the_consumers_timeout()
    {
        var registration = ResolveDatabaseRegistration();

        // An unset timeout is not null, it is Timeout.InfiniteTimeSpan — which is
        // negative, and therefore passes a naive "shorter than the consumer" comparison
        // while meaning the exact opposite. Ruled out first, on purpose.
        Assert.NotEqual(Timeout.InfiniteTimeSpan, registration.Timeout);
        Assert.True(registration.Timeout > TimeSpan.Zero);

        // The relationship, not the number. Whoever changes either side is made to look
        // at the other, which is the only thing that keeps "degraded" reachable — a
        // check bounded at 3s under a 2s consumer is as broken as no bound at all.
        Assert.True(
            registration.Timeout < DependencyInjection.ReadinessConsumerTimeout,
            $"The database check may run for {registration.Timeout}, but the BFF gives up after "
            + $"{DependencyInjection.ReadinessConsumerTimeout} (contract §3). The caller would give up first "
            + "and report the API unreachable instead of degraded.");

        // And strictly inside the contract's budget for the whole document, not merely
        // under the consumer's patience. Strictly, because the budget covers the response
        // around the check and every other check beside it: a check allowed the entire
        // budget overruns the document by construction, which is how the first attempt at
        // this bound was measured at 3.03s against a 3s contract.
        Assert.True(
            registration.Timeout < DependencyInjection.ReadinessDocumentBudget,
            $"The database check may run for {registration.Timeout}, but the whole readiness document "
            + $"must answer within {DependencyInjection.ReadinessDocumentBudget} (contract §1).");
    }

    [Fact]
    public async Task A_check_that_overruns_its_timeout_still_answers_degraded()
    {
        // A check that never returns, bounded at a tenth of a second: the same shape as
        // the real defect, at a speed a test suite can afford.
        using var factory = new FitForgeApiFactory
        {
            DatabaseDelay = TimeSpan.FromMinutes(1),
            DatabaseTimeout = TimeSpan.FromMilliseconds(100),
        };
        using var client = factory.CreateClient();

        var started = DateTimeOffset.UtcNow;
        using var response = await client.GetAsync("/health/ready");
        var elapsed = DateTimeOffset.UtcNow - started;

        // Not a 500, not a hang: the overrun is a verdict about a dependency, and the
        // contract has a word for it.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Readiness took {elapsed}, so the bound did not hold.");

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("degraded", document.GetProperty("status").GetString());

        var check = document.GetProperty("checks").EnumerateArray().Single();
        Assert.Equal("database", check.GetProperty("name").GetString());
        Assert.Equal("failed", check.GetProperty("status").GetString());

        // Duration is still reported, so the timeout is visible in the document rather
        // than only in a log the person reading this has no access to.
        Assert.True(check.GetProperty("durationMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task A_check_that_overruns_discloses_nothing_about_the_server()
    {
        using var factory = new FitForgeApiFactory
        {
            DatabaseDelay = TimeSpan.FromMinutes(1),
            DatabaseTimeout = TimeSpan.FromMilliseconds(100),
        };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        // The framework's own timeout description mentions the check by nothing worse
        // than name, but this endpoint is reachable by anything that can reach the API
        // (contract §1), so the timeout path gets the same scrutiny as the failure path.
        Assert.DoesNotContain("timeout occurred", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the registration <see cref="DependencyInjection.AddFitForgePersistence"/>
    /// actually produces. Deliberately not the test host: that one replaces the
    /// registrations, so asserting against it would prove only that the stub is correct.
    /// </summary>
    private static HealthCheckRegistration ResolveDatabaseRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddFitForgePersistence(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value
            .Registrations
            .Single(registration => registration.Name == DependencyInjection.DatabaseHealthCheckName);
    }
}
