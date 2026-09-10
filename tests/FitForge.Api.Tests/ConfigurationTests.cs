using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Tests;

/// <summary>
/// A missing connection string must stop the application at startup with a message
/// naming the setting — not surface later as an unhandled provider exception on a
/// developer's first request (spec, Edge Cases; FR-010).
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void Startup_without_a_connection_string_fails_and_names_the_setting()
    {
        // No UseSetting here: appsettings.json ships the key with an empty value, which
        // is exactly the state a developer who has not set the environment variable is in.
        using var factory = new WebApplicationFactory<Program>();

        var exception = Assert.ThrowsAny<OptionsValidationException>(() => factory.CreateClient());

        var message = string.Join(" ", exception.Failures);

        Assert.Contains("Database:ConnectionString", message, StringComparison.Ordinal);
        Assert.Contains("Database__ConnectionString", message, StringComparison.Ordinal);
        Assert.Contains("user-secrets", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_with_a_connection_string_succeeds_without_a_reachable_server()
    {
        // Validation checks that a value is present, never that it connects. Requiring a
        // live SQL Server to start would make the gate depend on somebody's machine.
        using var factory = new FitForgeApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.True(response.IsSuccessStatusCode);
    }
}
