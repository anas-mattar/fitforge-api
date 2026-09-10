using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FitForge.Api.Tests;

/// <summary>
/// Hosts the API for tests with no SQL Server anywhere in sight.
/// </summary>
/// <remarks>
/// <para>
/// Two things are substituted, and only two. Configuration gets a syntactically valid
/// connection string so that startup validation passes — the tests here are not about
/// whether a string is present, which <see cref="ConfigurationTests"/> covers separately.
/// The health-check registrations are replaced with a stub whose verdict the test chooses,
/// so both the 200 and the 503 path are reachable deterministically.
/// </para>
/// <para>
/// Substituting the check rather than the database is deliberate: a test that needs a
/// live SQL Server to prove a JSON shape is a test that will be skipped within a month.
/// </para>
/// </remarks>
internal sealed class FitForgeApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Verdict the stubbed database check reports. Defaults to healthy.</summary>
    public HealthStatus DatabaseStatus { get; init; } = HealthStatus.Healthy;

    /// <summary>
    /// Description and exception the stubbed check carries. Present so a disclosure test
    /// can prove the endpoint does not pass them through — a real SQL Server failure puts
    /// the server name and credentials in exactly these places.
    /// </summary>
    public string? DatabaseFailureDetail { get; init; }

    /// <summary>
    /// Makes the stubbed check take this long. Paired with <see cref="DatabaseTimeout"/>
    /// it reproduces a check that overruns its bound, without a test that waits real
    /// seconds to find out.
    /// </summary>
    public TimeSpan? DatabaseDelay { get; init; }

    /// <summary>
    /// Timeout carried by the stubbed registration. Unset, the stub has none — which is
    /// what every test that is not about timeouts wants.
    /// </summary>
    public TimeSpan? DatabaseTimeout { get; init; }

    /// <summary>
    /// Points the application at a real database. Left unset, the factory uses a string
    /// that is valid but unreachable, which is what every test that is not about
    /// persistence wants (see the remarks above).
    /// </summary>
    /// <remarks>
    /// Added in feature 002 phase 4, when the endpoint tests started needing rows.
    /// <c>SqlServerDatabase</c> supplies the value; nothing else should.
    /// </remarks>
    public string? ConnectionString { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Startup validates that the salt is present (feature 002 phase 4). A fixed value
        // is right here and is not a credential: it salts a hash of a source address, and
        // no test asserts what the salt IS - only that behaviour is consistent under one.
        builder.UseSetting("Security:SourceAddressSalt", "tests-only-salt");

        builder.UseSetting("Database:ConnectionString",
            ConnectionString ??
            "Server=(localdb)\\FitForgeTests;Database=FitForge;Trusted_Connection=True;TrustServerCertificate=True");

        builder.ConfigureTestServices(services =>
        {
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration(
                    name: "database",
                    factory: _ => new StubHealthCheck(DatabaseStatus, DatabaseFailureDetail, DatabaseDelay),
                    failureStatus: HealthStatus.Unhealthy,
                    tags: null,
                    timeout: DatabaseTimeout));
            });
        });
    }

    private sealed class StubHealthCheck(HealthStatus status, string? detail, TimeSpan? delay) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            if (delay is { } pause)
            {
                // Honours the token, as a real check must: the timeout is enforced by
                // cancelling the check, and a check that ignores cancellation overruns
                // its bound regardless of what the registration says.
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }

            var exception = detail is null ? null : new InvalidOperationException(detail);
            return new HealthCheckResult(status, detail, exception);
        }
    }
}
