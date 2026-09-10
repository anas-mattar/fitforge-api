using System.Security.Claims;
using System.Text.Encodings.Web;
using FitForge.Api.Features.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Hosting.Authentication;

/// <summary>
/// Turns <c>Authorization: Bearer &lt;token&gt;</c> into the current member, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// The BFF is the only caller (invariant 7, annotation A6). It holds the token in an
/// HttpOnly cookie the browser cannot read and attaches it server-side, so this header
/// never crosses a browser.
/// </para>
/// <para>
/// <b>Every failure is the same failure.</b> A missing header, a malformed one, an
/// unknown token, an expired session, a revoked session, and a session belonging to a
/// soft-deleted member all produce <see cref="AuthenticateResult.NoResult"/> and the same
/// 401 (<c>contracts/auth.md</c> §5). Distinguishing them would tell a caller that a
/// token was once valid.
/// </para>
/// </remarks>
public sealed class BearerSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    CurrentMember currentMember)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "FitForgeSession";

    private const string Prefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header[Prefix.Length..].Trim();

        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var resolved = await sessions.ResolveAsync(token, Context.RequestAborted);

        if (resolved is null)
        {
            return AuthenticateResult.NoResult();
        }

        currentMember.Set(resolved.Member, token, resolved.ExpiresAtUtc);

        // PublicId, never the internal Id (invariant 8). A claim is not a URL, payload or
        // log line, but it is one serialization away from being all three, and the
        // external identifier costs nothing here.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, resolved.Member.PublicId.ToString())],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
