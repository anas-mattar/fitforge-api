using System.Security.Cryptography;
using System.Text;
using FitForge.Domain.Members;
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
/// The API is the enforcement point, not the BFF (invariant 7). The BFF forwards the
/// caller's address; it does not decide what to do about it.
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
    /// Whether this attempt is allowed, and if not, how long to wait.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the attempt may proceed; otherwise the <c>Retry-After</c> value.
    /// </returns>
    /// <remarks>
    /// Checked <b>before</b> the password is verified, so a throttled request never pays
    /// the deliberate hashing cost. A throttle applied after the expensive work is a
    /// denial-of-service amplifier rather than a defence.
    /// </remarks>
    public async Task<TimeSpan?> RetryAfterAsync(
        string normalizedEmail,
        string? sourceAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var since = UtcNow() - Window;
        var source = HashSource(sourceAddress);

        var oldestEmailAttempt = await db.SignInAttempts
            .Where(a => a.NormalizedEmail == normalizedEmail && a.AttemptedAtUtc >= since)
            .OrderBy(a => a.AttemptedAtUtc)
            .Select(a => (DateTime?)a.AttemptedAtUtc)
            .Skip(MaxPerEmail - 1)
            .FirstOrDefaultAsync(cancellationToken);

        if (oldestEmailAttempt is { } emailAt)
        {
            return RetryAfterFrom(emailAt);
        }

        var oldestSourceAttempt = await db.SignInAttempts
            .Where(a => a.SourceHash == source && a.AttemptedAtUtc >= since)
            .OrderBy(a => a.AttemptedAtUtc)
            .Select(a => (DateTime?)a.AttemptedAtUtc)
            .Skip(MaxPerSource - 1)
            .FirstOrDefaultAsync(cancellationToken);

        return oldestSourceAttempt is { } sourceAt ? RetryAfterFrom(sourceAt) : null;
    }

    /// <summary>
    /// Records one failure. Called for <b>every</b> failed sign-in, including those for
    /// addresses no member has.
    /// </summary>
    public async Task RecordFailureAsync(
        string normalizedEmail,
        string? sourceAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        db.SignInAttempts.Add(new SignInAttempt
        {
            NormalizedEmail = normalizedEmail,
            SourceHash = HashSource(sourceAddress),
            AttemptedAtUtc = UtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Clears an address's bucket after a successful sign-in.
    /// </summary>
    /// <remarks>
    /// The <b>source</b> bucket is deliberately not cleared: one success does not license
    /// thirty more guesses from the same place, which is exactly what an attacker with one
    /// valid account of their own would use it for.
    /// </remarks>
    public async Task ClearEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        await db.SignInAttempts
            .Where(a => a.NormalizedEmail == normalizedEmail)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private TimeSpan RetryAfterFrom(DateTime oldestInWindow)
    {
        // When the oldest attempt still inside the window falls out of it, there is room
        // again. Reporting that instant is honest; reporting a flat fifteen minutes would
        // tell a member to wait longer than they have to.
        var wait = (oldestInWindow + Window) - UtcNow();

        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// SHA-256 over the salt and the address. A missing address hashes the empty string,
    /// so a caller that supplies none shares one bucket rather than escaping the count.
    /// </summary>
    private byte[] HashSource(string? sourceAddress) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            options.Value.SourceAddressSalt + "|" + (sourceAddress ?? string.Empty)));

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
