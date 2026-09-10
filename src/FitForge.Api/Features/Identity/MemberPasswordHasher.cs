using FitForge.Domain.Members;
using Microsoft.AspNetCore.Identity;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// The only place FitForge turns a password into a stored value, or checks one against it.
/// </summary>
/// <remarks>
/// <para>
/// <c>plan.md</c> D2. <see cref="PasswordHasher{TUser}"/>'s v3 format: PBKDF2-HMAC-SHA512,
/// a 128-bit random salt, a 256-bit subkey, written behind a format byte.
/// </para>
/// <para>
/// <b>Argon2id was not chosen, and it is the better algorithm.</b> The reason is dependency
/// governance rather than cryptography: every Argon2 implementation available to this
/// project is third-party, in the one place where "we will review it later" is not an
/// acceptable answer, and FitForge has nobody who can review a crypto library today. The
/// decision is reversible <i>because</i> of <see cref="Verify"/>'s rehash path, which is
/// built now rather than promised — a deferral with a mechanism, not a preference.
/// </para>
/// <para>
/// Registered as a singleton: the decoy hash below is computed once, at startup.
/// </para>
/// </remarks>
public sealed class MemberPasswordHasher
{
    /// <summary>
    /// OWASP's current floor for PBKDF2-HMAC-SHA512 (Password Storage Cheat Sheet).
    /// </summary>
    /// <remarks>
    /// A named constant carrying its citation, because this number exists to be **raised**
    /// and a magic literal never gets raised. When it changes, no member is locked out and
    /// no migration runs: <see cref="Verify"/> rehashes on the next successful sign-in.
    /// </remarks>
    public const int IterationCount = 210_000;

    private readonly IPasswordHasher<Member> _hasher;

    /// <summary>
    /// A real hash of a value nobody knows, kept so that a sign-in for an address that
    /// does not exist can do the same work as one that does.
    /// </summary>
    /// <remarks>
    /// <c>plan.md</c> D6. Without it the timing difference is not subtle — one path is a
    /// 210,000-iteration PBKDF2 and the other is an index miss, which is an existence
    /// oracle a stopwatch can read. Computed once here rather than per request, because a
    /// per-request hash of a random value would cost the stretch twice.
    /// </remarks>
    private readonly string _decoyHash;

    /// <summary>
    /// <see cref="PasswordHasher{TUser}"/> ignores the user instance entirely — the type
    /// parameter is a marker. One throwaway instance therefore serves every call, and no
    /// caller has to supply a <see cref="Member"/> it does not have.
    /// </summary>
    private static readonly Member Nobody = new();

    public MemberPasswordHasher(IPasswordHasher<Member> hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        _hasher = hasher;

        // 32 bytes from a cryptographic source. Not a constant, and not derived from
        // anything: if the decoy's plaintext were guessable, VerifyDecoy could be made to
        // return success and the timing defence would become a correctness bug.
        var unguessable = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _decoyHash = _hasher.HashPassword(Nobody, unguessable);
    }

    /// <summary>
    /// Hashes a password for storage. The result is a versioned composite; the plaintext
    /// is not recoverable from it (FR-003).
    /// </summary>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return _hasher.HashPassword(Nobody, password);
    }

    /// <summary>
    /// Checks <paramref name="password"/> against <paramref name="member"/>'s stored hash,
    /// upgrading the stored hash in place when its parameters are behind
    /// <see cref="IterationCount"/>.
    /// </summary>
    /// <returns>
    /// Whether the password matched, and whether <see cref="Member.PasswordHash"/> was
    /// rewritten — the caller persists it. Rewriting here and saving there keeps this type
    /// free of a <c>DbContext</c>.
    /// </returns>
    /// <remarks>
    /// The rehash happens inside the successful verification, which is the only moment the
    /// plaintext is available. Deferring it to "a migration later" is the same as never:
    /// there is no later moment at which the password can be re-derived.
    /// </remarks>
    public PasswordVerification Verify(Member member, string password)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(password);

        var result = _hasher.VerifyHashedPassword(member, member.PasswordHash, password);

        switch (result)
        {
            case PasswordVerificationResult.Success:
                return new PasswordVerification(Succeeded: true, Rehashed: false);

            case PasswordVerificationResult.SuccessRehashNeeded:
                member.PasswordHash = Hash(password);
                return new PasswordVerification(Succeeded: true, Rehashed: true);

            default:
                return new PasswordVerification(Succeeded: false, Rehashed: false);
        }
    }

    /// <summary>
    /// Does the work a real verification would, and discards the answer. Called when no
    /// member matches the submitted address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FR-004 requires "unknown email" and "wrong password" to be indistinguishable, and
    /// names timing explicitly. The response body and status are the endpoint's job
    /// (<c>contracts/auth.md</c> §3); the clock is this method's.
    /// </para>
    /// <para>
    /// It returns nothing on purpose. A method returning a bool here would eventually be
    /// branched on by someone who did not read this comment, and the branch would be the
    /// oracle.
    /// </para>
    /// </remarks>
    public void VerifyDecoy(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        _ = _hasher.VerifyHashedPassword(Nobody, _decoyHash, password);
    }
}

/// <summary>
/// The outcome of <see cref="MemberPasswordHasher.Verify"/>.
/// </summary>
/// <param name="Succeeded">Whether the password matched.</param>
/// <param name="Rehashed">
/// Whether the member's stored hash was rewritten and now needs saving.
/// </param>
public readonly record struct PasswordVerification(bool Succeeded, bool Rehashed);
