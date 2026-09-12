using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
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

    /// <summary>
    /// The address requests appear to arrive from. Defaults to the one the factory also
    /// declares trusted, so a test that sends <c>X-Forwarded-For</c> is believed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TestServer</c> simulates no transport, so <c>HttpContext.Connection.RemoteIpAddress</c>
    /// is null and <see cref="FitForge.Api.Features.Identity.SourceAddress"/> correctly
    /// answers "unknown" for every request. Supplying one is filling in the layer the test
    /// host does not have — not stubbing the code under test.
    /// </para>
    /// <para>
    /// The distinction matters here more than usual. Feature 002's finding F1 was a suite
    /// that stayed green because the throttle tests set the <c>X-Forwarded-For</c> header
    /// themselves, on a production path where nothing ever set it. Setting the peer address
    /// is the opposite move: the trust decision, the header parsing and which entry is read
    /// all still run for real, and a test can now make the peer untrusted and watch the
    /// header be ignored.
    /// </para>
    /// </remarks>
    public string PeerAddress { get; init; } = TrustedPeer;

    /// <summary>The one address this factory tells the API to believe.</summary>
    public const string TrustedPeer = "127.0.0.1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Startup validates that the salt is present (feature 002 phase 4). A fixed value
        // is right here and is not a credential: it salts a hash of a source address, and
        // no test asserts what the salt IS - only that behaviour is consistent under one.
        builder.UseSetting("Security:SourceAddressSalt", "tests-only-salt");

        // Startup also validates that at least one proxy is trusted (feature 002 phase 13).
        builder.UseSetting("Security:TrustedProxies:0", TrustedPeer);

        builder.UseSetting("Database:ConnectionString",
            ConnectionString ??
            "Server=(localdb)\\FitForgeTests;Database=FitForge;Trusted_Connection=True;TrustServerCertificate=True");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new PeerAddressFilter(IPAddress.Parse(PeerAddress)));

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

    /// <summary>
    /// Gives every request a connection to have come from, ahead of the whole pipeline.
    /// </summary>
    /// <remarks>
    /// An <see cref="IStartupFilter"/> rather than test middleware in the application, so
    /// nothing in <c>Program.cs</c> knows tests exist.
    /// </remarks>
    private sealed class PeerAddressFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                context.Connection.RemoteIpAddress = peer;
                await proceed();
            });

            next(app);
        };
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
