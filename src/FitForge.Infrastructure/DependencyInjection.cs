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
    /// it measures 2.04s warm and 2.83s cold.
    /// </para>
    /// <para>
    /// That leaves 0.17s of cold headroom, not the "second of headroom" an earlier version
    /// of this comment claimed, and checks run <em>sequentially</em> — so the budget does
    /// not survive a second dependency check at this timeout. Whoever adds one must lower
    /// both, or the contract's 3 seconds stops being true. The startup guard below catches
    /// this timeout crossing the budget; it cannot catch two timeouts summing past it.
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
    /// unreachable (contract §3). Restated here for the tests that assert the two bounds
    /// stay in the right order; the value is owned by the contract, not by this
    /// repository.
    /// </summary>
    public static readonly TimeSpan ReadinessConsumerTimeout = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddFitForgePersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Guard the bound that binds. Phase 6 checked this against
        // ReadinessConsumerTimeout, which is the loose one — 10 seconds of consumer
        // patience is not the constraint, the contract's 3-second document budget is, and
        // a value between the two would have satisfied the old guard while breaking the
        // contract. Checked in code and not only in a test because the number it is
        // measured against lives in another repository.
        if (DatabaseHealthCheckTimeout >= ReadinessDocumentBudget)
        {
            throw new InvalidOperationException(
                $"The database health check timeout ({DatabaseHealthCheckTimeout}) must be shorter than the "
                + $"contract's readiness budget ({ReadinessDocumentBudget}) — and shorter still once a second "
                + "dependency check exists, because checks run sequentially and share that budget "
                + "(contract §1).");
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
        // it produced. A loop rather than Single() because a caller may legitimately have
        // registered nothing yet — not, as this comment claimed until phase 8, because a
        // test host might have replaced the registrations first. It cannot: this delegate
        // is registered here and delegates run in registration order, so it always runs
        // before anything a test adds afterwards. The consequence is worth knowing — a
        // test host that clears and re-adds the registration replaces this bound with
        // whatever timeout it supplies, which is why the bound is asserted against a
        // freshly built ServiceCollection and never against the test host.
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
