using FitForge.Api.Hosting.Authentication;
using FitForge.Domain.Members;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// Registers password hashing. One extension, called once from <c>Program</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <c>AddIdentity</c>.</b> That call brings <c>UserManager</c>,
/// <c>SignInManager</c>, a store abstraction, a schema and a set of opinions about
/// authentication that would displace this feature's design — sessions the API can revoke
/// (<c>plan.md</c> D3), a <c>/me</c>-shaped surface (D7), a throttle that counts addresses
/// that do not exist (D5). Only <see cref="PasswordHasher{TUser}"/> is wanted, and it is
/// available from the shared framework, so only it is registered.
/// </para>
/// <para>
/// <c>plan.md</c> §5 approved <c>Microsoft.Extensions.Identity.Core</c> as this feature's
/// one new package. It turned out to be unnecessary: on .NET 10 the ASP.NET Core shared
/// framework already provides these types, and adding the reference produces NU1510
/// ("will not be pruned... likely unnecessary"), which <c>TreatWarningsAsErrors</c> turns
/// into a build failure. <b>Feature 002 therefore adds no package at all.</b> Same shape
/// as 001's <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> finding, and recorded for
/// the same reason: so nobody adds it back believing it was ever needed.
/// </para>
/// </remarks>
public static class IdentityServiceCollectionExtensions
{
    public static IServiceCollection AddFitForgePasswordHashing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<PasswordHasherOptions>(options =>
        {
            // IdentityV3 is the default, and it is named rather than assumed: it selects
            // PBKDF2-HMAC-SHA512 with the format byte the rehash path depends on. A future
            // default change that silently moved the format would otherwise be invisible
            // here (plan.md D2).
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
            options.IterationCount = MemberPasswordHasher.IterationCount;
        });

        services.AddSingleton<IPasswordHasher<Member>, PasswordHasher<Member>>();

        // Singleton because the decoy hash is computed in the constructor: the ~200ms
        // stretch is paid once at startup rather than on every sign-in for an address
        // that does not exist.
        services.AddSingleton<MemberPasswordHasher>();

        return services;
    }

    /// <summary>
    /// Registers session issue/resolve/revoke and the bearer scheme that uses them.
    /// </summary>
    /// <remarks>
    /// One scheme, no default challenge beyond 401: there is no login page to redirect
    /// to, because the API never renders one. The BFF owns that (<c>plan.md</c> D11).
    /// </remarks>
    public static IServiceCollection AddFitForgeSessions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: both hold or reach a DbContext, and CurrentMember is per request by
        // definition — a singleton would hand one member's identity to another's request,
        // which is invariant 2's worst failure produced by a DI lifetime.
        services.AddScoped<SessionService>();
        services.AddScoped<CurrentMember>();

        // TimeProvider.System unless a test replaced it. Injected rather than DateTime.UtcNow
        // so expiry and the touch interval are testable without waiting fourteen days.
        services.TryAddSingleton(TimeProvider.System);

        services
            .AddAuthentication(BearerSessionHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerSessionHandler>(
                BearerSessionHandler.SchemeName,
                configureOptions: null);

        services.AddAuthorization();

        return services;
    }
}
