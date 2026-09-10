using FitForge.Domain.Members;

namespace FitForge.Api.Hosting.Authentication;

/// <summary>
/// Who is asking. The <b>only</b> way a handler learns that.
/// </summary>
/// <remarks>
/// <para>
/// <c>plan.md</c> D7. Invariant 2 — every record belongs to exactly one member and never
/// leaves them — is upheld structurally in this feature: no route parameter, header or
/// body field names a member, so there is nothing for a handler to read but this.
/// </para>
/// <para>
/// Deliberately not a claims lookup at the call site. A handler that reaches into
/// <c>HttpContext.User</c> can reach for a different claim tomorrow; one that can only
/// call <see cref="Member"/> cannot. There is one place to be right.
/// </para>
/// <para>
/// Scoped, and populated by <c>BearerSessionHandler</c> during authentication.
/// </para>
/// </remarks>
public sealed class CurrentMember
{
    private Member? _member;
    private string? _token;
    private DateTime? _expiresAtUtc;

    /// <summary>
    /// The authenticated member.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The request was not authenticated. This is a programming error — an
    /// authenticated-only endpoint asked before authentication ran, or an anonymous
    /// endpoint asked at all — and it throws rather than returning null so it surfaces as
    /// a 500 in development instead of a null-reference somewhere downstream.
    /// </exception>
    public Member Member =>
        _member ?? throw new InvalidOperationException(
            "No authenticated member on this request. Endpoints that read member-owned " +
            "data must require authentication; anonymous endpoints must not ask.");

    /// <summary>
    /// The token this request presented, for the paths that revoke around it (FR-013).
    /// </summary>
    public string Token =>
        _token ?? throw new InvalidOperationException("No session token on this request.");

    /// <summary>
    /// When the presented session stops working, after any slide this request performed.
    /// </summary>
    public DateTime ExpiresAtUtc =>
        _expiresAtUtc ?? throw new InvalidOperationException("No session on this request.");

    /// <summary>Whether the request carries an authenticated member.</summary>
    public bool IsAuthenticated => _member is not null;

    internal void Set(Member member, string token, DateTime expiresAtUtc)
    {
        _member = member;
        _token = token;
        _expiresAtUtc = expiresAtUtc;
    }
}
