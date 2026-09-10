using FitForge.Api.Hosting.Authentication;
using FitForge.Domain.Members;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// The authentication surface. Phase 3 maps the one endpoint that only needs sessions;
/// register, sign-in and sign-out arrive in phase 4.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth");

        // contracts/auth.md §5. The 401 for an unresolvable token is produced by the
        // authorization pipeline, not here: by the time this delegate runs, the session
        // resolved. That is the point of doing it in a handler — the endpoint has no
        // "is this valid" branch to get wrong.
        group.MapGet("/session", (CurrentMember current) => Results.Ok(new
        {
            member = MemberSummary.From(current.Member),
            expiresAtUtc = current.ExpiresAtUtc,
        }))
        .RequireAuthorization()
        .WithName("GetSession");

        return app;
    }
}

/// <summary>
/// The member shape every contract in this feature returns (<c>contracts/auth.md</c> §8).
/// </summary>
/// <remarks>
/// Enum values go over the wire as <b>names</b>, not numbers: the column is a
/// <c>TINYINT</c> for storage, and a member-facing label should be renameable without a
/// migration. <c>memberSince</c> is an instant — formatting it as <c>19 Aug 2026</c>
/// (VI-024) is the browser's job, because only the browser knows the member's locale.
/// </remarks>
public sealed record MemberSummary(
    Guid PublicId,
    string Email,
    string DisplayName,
    DateTime MemberSince,
    string Units,
    string TimeZone,
    string Goal,
    string Experience)
{
    public static MemberSummary From(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new MemberSummary(
            member.PublicId,
            member.Email,
            member.DisplayName,
            member.CreatedAtUtc,
            member.Units.ToString(),
            member.TimeZone,
            member.Goal.ToString(),
            member.Experience.ToString());
    }
}
