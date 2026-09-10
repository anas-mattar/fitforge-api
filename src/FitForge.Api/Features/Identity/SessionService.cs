using System.Security.Cryptography;
using System.Text;
using FitForge.Domain.Members;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// Issues, resolves and revokes sessions. The only code that turns a token into a member.
/// </summary>
/// <remarks>
/// <para>
/// <c>plan.md</c> D3 and <c>contracts/auth.md</c> §1. A 256-bit random token goes to the
/// caller exactly once; only <c>SHA-256(token)</c> is stored, and there is no path that
/// reads a token back.
/// </para>
/// <para>
/// <b>Why not a JWT.</b> FR-006 (sign-out destroys the session server-side) and FR-013
/// (changing a password invalidates every other session) both require revocation the
/// server performs. A self-contained token plus a revocation list is a database lookup
/// per request <i>and</i> a token — strictly more machinery than the lookup alone.
/// </para>
/// </remarks>
public sealed class SessionService(FitForgeDbContext db, TimeProvider clock)
{
    /// <summary>How long a session lives, and how far each use pushes it out.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    /// <summary>
    /// The floor on how often a resolve writes. Without it every authenticated request
    /// would be a write, which is a cost paid on the hottest path in the product for
    /// precision nobody can perceive.
    /// </summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Creates a session for <paramref name="member"/> and returns its token.
    /// </summary>
    /// <returns>
    /// The token, base64url, 43 characters. <b>This is the only time it exists</b> — it is
    /// not stored and cannot be recovered.
    /// </returns>
    public async Task<string> IssueAsync(Member member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        var token = NewToken();
        var now = UtcNow();

        db.Sessions.Add(new Session
        {
            MemberId = member.Id,
            TokenHash = HashToken(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + Lifetime,
            LastSeenAtUtc = now,
        });

        await db.SaveChangesAsync(cancellationToken);

        return token;
    }

    /// <summary>
    /// Resolves a token to its member, sliding the expiry forward on success.
    /// </summary>
    /// <returns>
    /// The member and the session's expiry, or <c>null</c> if the token does not identify
    /// a live session. The expiry is returned because <c>contracts/auth.md</c> §5 puts it
    /// in the response — reading it from the row that was already fetched costs nothing,
    /// and re-deriving it at the endpoint would be a second source of the same fact.
    /// </returns>
    /// <remarks>
    /// <b>One return value for four failures</b> — unknown, expired, revoked, and
    /// "belongs to a soft-deleted member" — because <c>contracts/auth.md</c> §5 requires
    /// them to be indistinguishable to the caller. Handing back a reason would let a
    /// caller learn that a token was once valid.
    /// <para>
    /// The soft-delete case is upheld by the join rather than by a check: <c>Members</c>
    /// carries a global query filter, so a soft-deleted member simply is not there to
    /// join to. There is no branch to forget.
    /// </para>
    /// </remarks>
    public async Task<ResolvedSession?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = HashToken(token);
        var now = UtcNow();

        var found = await db.Sessions
            .Where(s => s.TokenHash == hash && s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .Join(db.Members, s => s.MemberId, m => m.Id, (s, m) => new { Session = s, Member = m })
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            return null;
        }

        if (now - found.Session.LastSeenAtUtc >= TouchInterval)
        {
            found.Session.LastSeenAtUtc = now;
            found.Session.ExpiresAtUtc = now + Lifetime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ResolvedSession(found.Member, found.Session.ExpiresAtUtc);
    }

    /// <summary>
    /// Revokes the session a token identifies. Idempotent: an unknown or already-revoked
    /// token is a no-op, so a caller learns nothing from calling twice (FR-006).
    /// </summary>
    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = HashToken(token);

        await db.Sessions
            .Where(s => s.TokenHash == hash && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAtUtc, UtcNow()), cancellationToken);
    }

    /// <summary>
    /// Revokes every live session for a member except the one presented — FR-013's
    /// "every other session" after a password change.
    /// </summary>
    /// <remarks>
    /// The member keeps the tab they are working in. Signing them out of it as well would
    /// be defensible, but it makes changing a password feel like a punishment, and the
    /// session that just proved the current password is the one least likely to be stolen.
    /// </remarks>
    public async Task RevokeAllExceptAsync(long memberId, string keepToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keepToken);

        var keep = HashToken(keepToken);

        await db.Sessions
            .Where(s => s.MemberId == memberId && s.RevokedAtUtc == null && s.TokenHash != keep)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAtUtc, UtcNow()), cancellationToken);
    }

    /// <summary>
    /// Revokes every live session for a member, the presented one included — account
    /// deletion (FR-014).
    /// </summary>
    public async Task RevokeAllAsync(long memberId, CancellationToken cancellationToken)
    {
        await db.Sessions
            .Where(s => s.MemberId == memberId && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAtUtc, UtcNow()), cancellationToken);
    }

    /// <summary>
    /// 256 bits from a cryptographic source, base64url without padding.
    /// </summary>
    /// <remarks>
    /// <see cref="RandomNumberGenerator"/>, never <see cref="Random"/>: a guessable token
    /// is an account takeover, and the two type names are one letter apart in an
    /// autocomplete list.
    /// </remarks>
    private static string NewToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The stored form. Unsalted and fast on purpose — see <see cref="Session.TokenHash"/>.
    /// </summary>
    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Always UTC (invariant 4), and always through <see cref="TimeProvider"/> so expiry
    /// and the touch interval are testable without waiting an hour.
    /// </summary>
    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}

/// <summary>
/// A live session: who it belongs to, and when it stops working.
/// </summary>
/// <param name="Member">The authenticated member.</param>
/// <param name="ExpiresAtUtc">
/// After any slide this resolve performed, so the caller reports the value that is now
/// stored rather than the one it replaced.
/// </param>
public sealed record ResolvedSession(Member Member, DateTime ExpiresAtUtc);
