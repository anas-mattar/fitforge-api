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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:ConnectionString",
            "Server=(localdb)\\FitForgeTests;Database=FitForge;Trusted_Connection=True;TrustServerCertificate=True");

        builder.ConfigureTestServices(services =>
        {
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration(
                    name: "database",
                    factory: _ => new StubHealthCheck(DatabaseStatus, DatabaseFailureDetail),
                    failureStatus: HealthStatus.Unhealthy,
                    tags: null));
            });
        });
    }

    private sealed class StubHealthCheck(HealthStatus status, string? detail) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            var exception = detail is null ? null : new System.InvalidOperationException(detail);
            return Task.FromResult(new HealthCheckResult(status, detail, exception));
        }
    }
}
