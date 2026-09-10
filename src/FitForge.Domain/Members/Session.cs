namespace FitForge.Domain.Members;

/// <summary>
/// What the BFF's cookie references and the API can revoke.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a business entity</b>, and so deliberately not a <see cref="BusinessEntity"/>.
/// <c>specs/002-identity-member-profile/data-model.md</c> records three deviations from
/// <c>docs/rulebooks/database-rules.md</c>, approved in <c>plan.md</c> D3:
/// </para>
/// <list type="number">
/// <item><b>No <c>PublicId</c>.</b> A session has no external identity — the token is the
/// only handle. Minting a second identifier for a secret-bearing row would create a way
/// to name a session that no contract needs.</item>
/// <item><b>No <c>CreatedBy</c> / <c>UpdatedBy</c>.</b> <see cref="MemberId"/> is the
/// actor, and a session is written only by the authentication paths.</item>
/// <item><b>No soft delete.</b> <see cref="RevokedAtUtc"/> is the tombstone and dead rows
/// are physically removed by the retention service. Invariant 3 protects master data a
/// member's training log references; nothing references a session, and keeping dead ones
/// forever would be a growing table of secret-adjacent rows for no benefit.</item>
/// </list>
/// <para>
/// <b>Only the hash of the token is stored.</b> A read of this table — a backup, a support
/// query, a leak — yields no usable session: SHA-256 is not reversible, and with 256 bits
/// of entropy the token is not searchable either.
/// </para>
/// </remarks>
public class Session
{
    public long Id { get; set; }

    /// <summary>
    /// The owning member's internal key. There is no navigation property: resolution
    /// joins <c>Members</c> explicitly so the soft-delete filter on that table decides
    /// whether the session is usable, rather than a filter on a navigation.
    /// </summary>
    public long MemberId { get; set; }

    /// <summary>
    /// SHA-256 of the token. Never the token.
    /// </summary>
    /// <remarks>
    /// SHA-256 rather than the password hash of <c>plan.md</c> D2: this input is already
    /// high-entropy random, so the slow salted hash a password needs would buy nothing
    /// and would cost that stretch on <b>every authenticated request</b>. The two hashes
    /// protect different things — "use the strong one everywhere" is the plausible
    /// mistake here.
    /// </remarks>
    public byte[] TokenHash { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When the session stops working. Slides forward on use (<c>contracts/auth.md</c> §5).
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Set means dead, whatever <see cref="ExpiresAtUtc"/> says.
    /// </summary>
    /// <remarks>
    /// FR-006 and FR-013 require revocation the server can perform. A self-contained
    /// token would make this column impossible, which is why <c>plan.md</c> D3 excludes
    /// one.
    /// </remarks>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>
    /// Last successful resolve, written at most hourly so the hot path is not a write per
    /// request.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; }
}
