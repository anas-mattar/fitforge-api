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

    /// <summary>
    /// How long the database check may run before it is failed on its own terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a performance target. Without it a cold attempt against an unreachable SQL
    /// Server takes about fifteen seconds, the consumer gives up first, and "the database
    /// is down" arrives as "the API is down" — losing the one distinction the contract
    /// exists to preserve. <c>Connect Timeout</c> in the connection string does not bound
    /// this: it was measured at 2 seconds and the check still took 14.7.
    /// </para>
    /// <para>
    /// Two seconds, not the three the contract gives the document: three bounds the whole
    /// readiness response, and a check bounded at exactly three cannot fit inside it. At
    /// three the document measured 3.03s — over the bound it was meant to satisfy. At two
    /// it measures 2.04s warm and 2.83s cold, and the second of headroom is what the next
    /// dependency check will be spending.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DatabaseHealthCheckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The contract's bound on the whole readiness document (contract §1). Every check's
    /// own timeout must be strictly shorter than this, with room for the others.
    /// </summary>
    public static readonly TimeSpan ReadinessDocumentBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the BFF waits for readiness before giving up and calling the API
    /// unreachable (contract §3). The value is owned by the contract, not by this
    /// repository; it is restated here only so the relationship below can be checked.
    /// </summary>
    public static readonly TimeSpan ReadinessConsumerTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddFitForgePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The bound means nothing unless it stays under the consumer's patience. Checked
        // here and not only in a test, because the two numbers live in different
        // repositories and whoever widens this one will be editing this file.
        if (DatabaseHealthCheckTimeout >= ReadinessConsumerTimeout)
        {
            throw new InvalidOperationException(
                $"The database health check timeout ({DatabaseHealthCheckTimeout}) must be shorter than the "
                + $"BFF's readiness timeout ({ReadinessConsumerTimeout}): otherwise the caller gives up first "
                + "and a degraded API is reported as an unreachable one (contract §1).");
        }

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

        // AddDbContextCheck takes no timeout, so the bound is applied to the registration
        // it produced. Written as a loop over matches rather than Single() so that a test
        // host which has already replaced the registrations is not made to crash here.
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations)
            {
                if (registration.Name == DatabaseHealthCheckName)
                {
                    registration.Timeout = DatabaseHealthCheckTimeout;
                }
            }
        });

        return services;
    }
}
