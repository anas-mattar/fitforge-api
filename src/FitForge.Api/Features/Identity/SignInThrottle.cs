using System.Security.Cryptography;
using System.Text;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// Counts failed sign-ins and refuses when there have been too many (FR-016,
/// <c>contracts/auth.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// Two fixed fifteen-minute windows: ten failures per submitted address, thirty per
/// source. Fixed rather than sliding — a sliding window needs per-attempt timestamps kept
/// longer and buys accuracy nobody can perceive.
/// </para>
/// <para>
/// <b>The rule that carries the design.</b> The email bucket counts attempts against
/// addresses that <b>do not exist</b> exactly as it counts attempts against ones that do.
/// A throttle that only fired for real accounts would make 429-versus-401 an existence
/// oracle — the very thing <c>MemberPasswordHasher.VerifyDecoy</c> spends 210,000
/// iterations to close. Getting this backwards would pass every test written from the
/// happy path, which is why <c>tasks.md</c> T044 tests the nonexistent address
/// specifically.
/// </para>
/// <para>
/// <b>Record first, then verify.</b> The attempt is written by the same statement that
/// decides, before any password is hashed. The order matters: recording afterwards left a
/// two-hundred-millisecond gap in which every concurrent attempt read the same stale count
/// (finding F2). It also means an attempt that is abandoned mid-hash still counts, which
/// is what an attacker who hangs up on every response was relying on.
/// </para>
/// <para>
/// <b>A successful sign-in clears the whole email bucket</b>, which removes the row that
/// attempt just wrote — so a successful <i>sign-in</i> costs nothing in either bucket.
/// <b>Registration is the exception</b>: §6 as amended on 2026-09-12 counts every register
/// attempt whatever its outcome, so <c>/auth/register</c> does not clear. Clearing there
/// deleted the row the attempt had just written and returned the per-source count to zero
/// after every success, which left the endpoint uncapped (finding F3, failure scenario
/// (b)).
/// </para>
/// <para>
/// The API is the enforcement point, not the BFF (invariant 7). The BFF forwards the
/// caller's address; it does not decide what to do about it, and
/// <see cref="SourceAddress"/> decides whether to believe it.
/// </para>
/// </remarks>
public sealed class SignInThrottle(
    FitForgeDbContext db,
    TimeProvider clock,
    IOptions<SignInThrottleOptions> options)
{
    /// <summary>Failures allowed per submitted address before the window closes.</summary>
    public const int MaxPerEmail = 10;

    /// <summary>Failures allowed per source address before the window closes.</summary>
    public const int MaxPerSource = 30;

    /// <summary>How far back either count looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Records this attempt and says whether it may proceed.
    /// </summary>
    /// <param name="sourceAddress">
    /// The caller, or <see langword="null"/> when <see cref="SourceAddress"/> could not
    /// establish one. Unknown means the per-source bucket is not consulted at all — never
    /// that everyone shares one.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the attempt was recorded and may proceed; otherwise the
    /// <c>Retry-After</c> value, and nothing was recorded.
    /// </returns>
    /// <remarks>
    /// Called <b>before</b> the password is verified, so a throttled request never pays the
    /// deliberate hashing cost. A throttle applied after the expensive work is a
    /// denial-of-service amplifier rather than a defence.
    /// </remarks>
    public async Task<TimeSpan?> TryRecordAsync(
        string normalizedEmail,
        string? sourceAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = UtcNow();
        var since = now - Window;
        var source = HashSource(sourceAddress);
        var countSource = sourceAddress is not null;

        var recorded = await db.TryRecordSignInAttemptAsync(
            normalizedEmail,
            source,
            countSource,
            now,
            since,
            MaxPerEmail,
            MaxPerSource,
            cancellationToken);

        return recorded
            ? null
            : await RetryAfterAsync(normalizedEmail, source, countSource, since, cancellationToken);
    }

    /// <summary>
    /// Clears an address's bucket after a password is used successfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>source</b> bucket is deliberately not cleared: one success does not license
    /// thirty more guesses from the same place, which is exactly what an attacker with one
    /// valid account of their own would use it for. What this does remove from the source
    /// count is the rows carrying this email — including the one the successful attempt
    /// itself wrote, which is how a success stays uncounted in both buckets.
    /// </para>
    /// <para>
    /// That last sentence is also this method's hazard, and the reason
    /// <c>/auth/register</c> no longer calls it. Removing the attempt's own row is
    /// harmless where the caller had to <i>prove</i> a password to get here — sign-in and
    /// the two <c>/me</c> re-authentications — because proving one is not something an
    /// attacker can repeat at will. Registration proves nothing, so the same removal let
    /// an unauthenticated caller wipe its own cost forever (finding F3, failure
    /// scenario (b)).
    /// </para>
    /// </remarks>
    public async Task ClearEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        await db.SignInAttempts
            .Where(a => a.NormalizedEmail == normalizedEmail)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// How long until the fuller of the two buckets has room for one more attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <c>n</c>-th most recent attempt, not the <c>n</c>-th oldest.</b> A bucket of
    /// limit <c>n</c> has room again once the <c>n</c>-th most recent attempt falls out of
    /// the window, because at most <c>n - 1</c> then remain. Reading the <c>n</c>-th
    /// <i>oldest</i> instead — which is what this did until finding N1 — points at the
    /// newest row in a just-full bucket and tells a member to wait a full fifteen minutes
    /// for a slot that opens in seconds.
    /// </para>
    /// <para>
    /// Both buckets are consulted and the longer wait wins: being under one limit is no
    /// use while the other still refuses.
    /// </para>
    /// </remarks>
    private async Task<TimeSpan> RetryAfterAsync(
        string normalizedEmail,
        byte[] source,
        bool countSource,
        DateTime since,
        CancellationToken cancellationToken)
    {
        var emailAt = await NthMostRecentAsync(
            db.SignInAttempts.Where(a => a.NormalizedEmail == normalizedEmail && a.AttemptedAtUtc >= since),
            MaxPerEmail,
            cancellationToken);

        var sourceAt = countSource
            ? await NthMostRecentAsync(
                db.SignInAttempts.Where(a => a.SourceHash == source && a.AttemptedAtUtc >= since),
                MaxPerSource,
                cancellationToken)
            : null;

        var waits = new[] { WaitFor(emailAt), WaitFor(sourceAt) };

        // At least a second, always. A zero or negative Retry-After reads as "go ahead"
        // and would turn a refusal into an invitation to retry in a tight loop.
        return waits.Max() is { } longest && longest > TimeSpan.FromSeconds(1)
            ? longest
            : TimeSpan.FromSeconds(1);
    }

    private Task<DateTime?> NthMostRecentAsync(
        IQueryable<Domain.Members.SignInAttempt> inWindow,
        int n,
        CancellationToken cancellationToken) =>
        inWindow
            .OrderByDescending(a => a.AttemptedAtUtc)
            .Select(a => (DateTime?)a.AttemptedAtUtc)
            .Skip(n - 1)
            .FirstOrDefaultAsync(cancellationToken);

    private TimeSpan? WaitFor(DateTime? nthMostRecent) =>
        nthMostRecent is { } at ? (at + Window) - UtcNow() : null;

    /// <summary>
    /// SHA-256 over the salt and the address.
    /// </summary>
    /// <remarks>
    /// An unknown address hashes the empty string. That value is a placeholder and never a
    /// bucket: <c>TryRecordAsync</c> does not count the source when the address is unknown,
    /// so these rows exist only to carry the per-email count and can never be reached by a
    /// caller whose address <i>is</i> known.
    /// </remarks>
    private byte[] HashSource(string? sourceAddress) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            options.Value.SourceAddressSalt + "|" + (sourceAddress ?? string.Empty)));

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
