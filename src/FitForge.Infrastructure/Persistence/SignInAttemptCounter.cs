using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Infrastructure.Persistence;

/// <summary>
/// The sign-in throttle's decision, as one batch that cannot interleave with another.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is here and not beside the throttle.</b> It is the only provider-specific
/// SQL in the product, and ADR-001 §4.3 puts anything that knows it is talking to SQL
/// Server in this project. The policy — how many attempts, how wide the window, how an
/// address is hashed — stays in <c>FitForge.Api</c>; only the atomicity lives here.
/// </para>
/// <para>
/// <b>Why any of this is needed.</b> The throttle used to count, then verify a password,
/// then record the failure: a check-then-act with a deliberate 210,000-iteration hash
/// sitting in the gap (feature 002, finding F2). Five hundred simultaneous attempts all
/// counted zero, all proceeded, and FR-016 held only for an attacker polite enough to go
/// one at a time.
/// </para>
/// </remarks>
public static class SignInAttemptCounter
{
    /// <summary>
    /// Counts inside a critical section keyed by the buckets, then inserts if both have
    /// room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named locks rather than lock hints, and the route here is worth recording.</b>
    /// The obvious shape — count both buckets under <c>UPDLOCK, HOLDLOCK</c>, then insert —
    /// deadlocked under the parallel test within seconds. Splitting the two counts into
    /// ordered statements did not help either, and the deadlock graph said why: the cycle
    /// was three processes deep and entirely inside <c>IX_SignInAttempt_Email_At</c>, every
    /// lock a <c>RangeS-U</c>. A key-range lock is taken per key as a scan walks a range,
    /// so concurrent scans over a range that other transactions are inserting into acquire
    /// their locks in an order nobody controls. There is no statement ordering that fixes
    /// that, because the resources are not two things — they are however many keys the
    /// window happens to hold.
    /// </para>
    /// <para>
    /// <c>sp_getapplock</c> replaces "however many keys" with exactly two named resources,
    /// always taken email-first. A deadlock needs two callers to acquire two resources in
    /// opposite orders; no caller here can, so the cycle cannot be formed rather than being
    /// unlikely. Inside the section the counts need no hint at all: whoever holds the pair
    /// is the only writer for those buckets, and the previous holder's row is committed and
    /// visible before the lock is handed over.
    /// </para>
    /// <para>
    /// The locks release when the transaction ends — <c>@LockOwner = 'Transaction'</c> — so
    /// there is no path where a failure leaves one held.
    /// </para>
    /// </remarks>
    private const string Open = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;

        DECLARE @emailCount INT, @sourceCount INT = 0, @got INT;

        SET @recorded = 0;

        BEGIN TRANSACTION;

        EXEC @got = sp_getapplock @Resource = @emailLock, @LockMode = 'Exclusive',
                                  @LockOwner = 'Transaction', @LockTimeout = 15000;
        IF @got < 0 THROW 50001, 'The sign-in throttle could not take its email lock.', 1;
        """;

    /// <summary>
    /// The source half of the critical section, present only when the address is known.
    /// </summary>
    /// <remarks>
    /// Left out entirely rather than keyed on a sentinel: an unknown address has no bucket,
    /// and a lock nobody needs would serialize every caller whose address is unknown
    /// against every other — which is finding F1's shared bucket wearing a different hat.
    /// </remarks>
    private const string LockSource = """
        EXEC @got = sp_getapplock @Resource = @sourceLock, @LockMode = 'Exclusive',
                                  @LockOwner = 'Transaction', @LockTimeout = 15000;
        IF @got < 0 THROW 50002, 'The sign-in throttle could not take its source lock.', 1;

        SELECT @sourceCount = COUNT(*)
        FROM [SignInAttempt]
        WHERE [SourceHash] = @source AND [AttemptedAtUtc] >= @since;
        """;

    private const string Decide = """
        SELECT @emailCount = COUNT(*)
        FROM [SignInAttempt]
        WHERE [NormalizedEmail] = @email AND [AttemptedAtUtc] >= @since;
        """;

    private const string Record = """
        IF @emailCount < @maxEmail AND @sourceCount < @maxSource
        BEGIN
            INSERT INTO [SignInAttempt] ([NormalizedEmail], [SourceHash], [AttemptedAtUtc])
            VALUES (@email, @source, @now);

            SET @recorded = 1;
        END

        COMMIT TRANSACTION;
        """;

    /// <summary>
    /// Records one attempt if, and only if, neither bucket is already full.
    /// </summary>
    /// <param name="countSource">
    /// <see langword="false"/> when the caller's address is unknown. The row is still
    /// written — <c>SourceHash</c> is <c>NOT NULL</c> and the per-email bucket still needs
    /// it — but the per-source bucket is neither consulted nor, since no known address ever
    /// hashes to the same value, countable. Treating "unknown" as an address is finding
    /// F1's outage.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the attempt was recorded and may proceed;
    /// <see langword="false"/> when a bucket was full and nothing was written.
    /// </returns>
    public static async Task<bool> TryRecordSignInAttemptAsync(
        this FitForgeDbContext db,
        string normalizedEmail,
        byte[] sourceHash,
        bool countSource,
        DateTime attemptedAtUtc,
        DateTime windowOpenedAtUtc,
        int maxPerEmail,
        int maxPerSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        ArgumentNullException.ThrowIfNull(sourceHash);

        // Email lock first, always. That fixed order is the entire deadlock argument.
        var sql = countSource
            ? string.Join('\n', Open, Decide, LockSource, Record)
            : string.Join('\n', Open, Decide, Record);

        // An output parameter rather than the batch's rows-affected. A batch that sets
        // variables reports a row count for each assignment as well as for the insert, so
        // the number coming back would depend on which branches ran — a decision this code
        // would then have to reverse-engineer. One integer, set in one place, says it
        // outright.
        var recorded = new SqlParameter("@recorded", SqlDbType.Int)
        {
            Direction = ParameterDirection.Output,
        };

        // Typed explicitly rather than inferred. An inferred byte[] parameter becomes
        // varbinary(max), and comparing that against a binary(32) column converts the
        // COLUMN — which loses the index seek that keeps these counts cheap.
        var parameters = new object[]
        {
            new SqlParameter("@email", SqlDbType.NVarChar, 254) { Value = normalizedEmail },
            new SqlParameter("@source", SqlDbType.Binary, 32) { Value = sourceHash },
            new SqlParameter("@now", SqlDbType.DateTime2) { Scale = 3, Value = attemptedAtUtc },
            new SqlParameter("@since", SqlDbType.DateTime2) { Scale = 3, Value = windowOpenedAtUtc },
            new SqlParameter("@maxEmail", SqlDbType.Int) { Value = maxPerEmail },
            new SqlParameter("@maxSource", SqlDbType.Int) { Value = maxPerSource },
            new SqlParameter("@emailLock", SqlDbType.NVarChar, 255) { Value = EmailLock(normalizedEmail) },
            new SqlParameter("@sourceLock", SqlDbType.NVarChar, 255) { Value = SourceLock(sourceHash) },
            recorded,
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

        return recorded.Value is int result && result == 1;
    }

    /// <summary>
    /// Names the email's critical section.
    /// </summary>
    /// <remarks>
    /// Hashed rather than used directly: <c>sp_getapplock</c> takes at most 255 characters,
    /// an address may be 254 of them, and a prefix would not fit. The hash needs no salt —
    /// this value is never stored and never leaves the statement, unlike
    /// <c>SignInAttempt.SourceHash</c>, which is both.
    /// </remarks>
    private static string EmailLock(string normalizedEmail) =>
        "fitforge-signin-email-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));

    private static string SourceLock(byte[] sourceHash) =>
        "fitforge-signin-source-" + Convert.ToHexString(sourceHash);
}
