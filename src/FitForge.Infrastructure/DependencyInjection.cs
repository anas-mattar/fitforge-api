using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace FitForge.Infrastructure;

/// <summary>
/// The single entry point by which the host wires up persistence.
/// </summary>
/// <remarks>
/// ADR-001 §4.3: the host asks for persistence, it does not configure it. Everything the
/// database needs — options, provider, health check — is decided here, so the API project
/// never names a connection string or a provider.
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Name reported for the database in the readiness document (contract §1).</summary>
    public const string DatabaseHealthCheckName = "database";

    public static IServiceCollection AddFitForgePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ValidateOnStart is the point of this block: a missing connection string must
        // fail at startup with a message naming the setting, not at the first request
        // with an unhandled provider exception (spec, Edge Cases).
        // Read and validated explicitly rather than through the reflection-based helpers:
        // OptionsBuilder.Bind and .ValidateDataAnnotations live in packages plan §5 does
        // not approve, and pulling two in to copy and null-check one string is a poor
        // trade. When DatabaseOptions grows a second setting, amend the plan and bind
        // properly rather than repeating this by hand.
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, config) =>
                options.ConnectionString =
                    config[$"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.ConnectionString)}"]
                    ?? string.Empty)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                DatabaseOptions.MissingConnectionStringMessage)
            .ValidateOnStart();

        services.AddDbContext<FitForgeDbContext>((provider, options) =>
        {
            var databaseOptions = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(databaseOptions.ConnectionString);
        });

        // Checks the context we already own rather than opening a second connection of
        // its own — see plan §5's amendment for why this is the first-party package.
        services.AddHealthChecks()
            .AddDbContextCheck<FitForgeDbContext>(
                name: DatabaseHealthCheckName,
                failureStatus: HealthStatus.Unhealthy);

        return services;
    }
}
