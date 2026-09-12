using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FitForge.Api.Features.Identity;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Tests;

/// <summary>
/// T116–T118 — the throttle's decision itself, against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Below HTTP on purpose. <see cref="CredentialEndpointTests"/> proves the endpoints refuse;
/// these prove the arithmetic and the atomicity underneath, and they can drive two hundred
/// simultaneous attempts in the time one sign-in spends hashing a password.
/// </para>
/// <para>
/// The engine matters here more than anywhere else in the suite: what makes
/// <c>TryRecordAsync</c> atomic is a lock SQL Server takes over an index range. No other
/// provider takes it, so no other provider would be testing this code.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class SignInThrottleTests(SqlServerDatabase database)
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IOptions<SignInThrottleOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new SignInThrottleOptions
        {
            SourceAddressSalt = "tests-only-salt",
            TrustedProxies = ["127.0.0.1"],
        });

    private SignInThrottle Throttle(FitForgeDbContext context, TimeProvider clock) =>
        new(context, clock, Options());

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    /// <summary>
    /// A source nothing else in the class shares.
    /// </summary>
    /// <remarks>
    /// The collection fixture gives the whole class one database, so two tests picking the
    /// same address would count each other's attempts. The throttle only ever hashes this
    /// value for equality and never parses it, so a unique string is as good as a unique
    /// address and is the only one that cannot collide.
    /// </remarks>
    private static string NewSource() => $"203.0.113.1#{Guid.NewGuid():N}";

    // ---- T116: the buckets count what §6 says they count -----------------------------

    [Fact]
    public async Task The_email_bucket_allows_ten_and_refuses_the_eleventh()
    {
        await using var context = database.NewContext();
        var throttle = Throttle(context, new FixedClock(Noon));
        var email = NewEmail();
        var source = NewSource();

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerEmail; attempt++)
        {
            Assert.Null(await throttle.TryRecordAsync(email, source, CancellationToken.None));
        }

        Assert.NotNull(await throttle.TryRecordAsync(email, source, CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_attempt_writes_no_row()
    {
        // Otherwise a continuous attacker refills the window with attempts that were never
        // allowed, and the member on the other end is locked out for as long as the
        // attacker keeps going rather than for fifteen minutes.
        await using var context = database.NewContext();
        var throttle = Throttle(context, new FixedClock(Noon));
        var email = NewEmail();
        var source = NewSource();

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerEmail; attempt++)
        {
            await throttle.TryRecordAsync(email, source, CancellationToken.None);
        }

        var before = await CountAsync(email);
        await throttle.TryRecordAsync(email, source, CancellationToken.None);
        await throttle.TryRecordAsync(email, source, CancellationToken.None);

        Assert.Equal(before, await CountAsync(email));
    }

    [Fact]
    public async Task The_source_bucket_allows_thirty_addresses_and_refuses_the_thirty_first()
    {
        // A different email every time, so nothing here can be the email bucket firing.
        await using var context = database.NewContext();
        var throttle = Throttle(context, new FixedClock(Noon));
        var source = NewSource();

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerSource; attempt++)
        {
            Assert.Null(await throttle.TryRecordAsync(NewEmail(), source, CancellationToken.None));
        }

        Assert.NotNull(await throttle.TryRecordAsync(NewEmail(), source, CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_source_is_not_a_bucket_that_everyone_shares()
    {
        // Finding F1, as an assertion. Until phase 13 the BFF never sent the header, so
        // every attempt in the product hashed the same empty string into ONE per-source
        // bucket: thirty failures from anywhere locked out every member at once. Unknown
        // now means the per-source bucket is not consulted, so this loop runs well past
        // MaxPerSource without a single refusal.
        await using var context = database.NewContext();
        var throttle = Throttle(context, new FixedClock(Noon));

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerSource * 2; attempt++)
        {
            Assert.Null(await throttle.TryRecordAsync(NewEmail(), sourceAddress: null, CancellationToken.None));
        }

        // And the per-email bucket still bites, so "unknown" weakens the defence rather
        // than removing it.
        var email = NewEmail();

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerEmail; attempt++)
        {
            Assert.Null(await throttle.TryRecordAsync(email, sourceAddress: null, CancellationToken.None));
        }

        Assert.NotNull(await throttle.TryRecordAsync(email, sourceAddress: null, CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_an_email_removes_its_rows_from_the_source_count_too()
    {
        // Which is what lets a successful sign-in cost nothing in either bucket without a
        // second delete: the attempt it recorded carries its email, so clearing the email
        // takes that row out of the source count as well.
        await using var context = database.NewContext();
        var throttle = Throttle(context, new FixedClock(Noon));
        var email = NewEmail();
        var source = NewSource();

        await throttle.TryRecordAsync(email, source, CancellationToken.None);
        await throttle.ClearEmailAsync(email, CancellationToken.None);

        await using var fresh = database.NewContext();
        Assert.Equal(0, await fresh.SignInAttempts.CountAsync(a => a.NormalizedEmail == email));
    }

    // ---- T117: parallel attempts are throttled as strictly as serial ones -------------

    [Fact]
    public async Task Sixty_four_simultaneous_attempts_get_exactly_ten_through()
    {
        // Finding F2, and the reason the decision is one statement rather than a count
        // followed by an insert. The old shape checked, spent ~200ms hashing, then
        // recorded — so every attempt that arrived inside that window read the same stale
        // zero. Five hundred parallel guesses all proceeded, and FR-016 held only against
        // an attacker considerate enough to go one at a time.
        //
        // Each task gets its own DbContext: a DbContext is not thread-safe, and sharing one
        // would serialize the very thing under test and turn this into a test that always
        // passes.
        //
        // Sixty-four rather than the five hundred the finding describes: one connection per
        // attempt, and the default pool holds a hundred. Six times the limit already fails
        // loudly against a check-then-act, and a test that exhausts the pool would fail for
        // a reason that has nothing to do with the throttle.
        const int Simultaneous = 64;

        var email = NewEmail();
        var source = NewSource();
        var contexts = new List<FitForgeDbContext>();

        try
        {
            var attempts = Enumerable.Range(0, Simultaneous).Select(_ =>
            {
                var context = database.NewContext();
                contexts.Add(context);
                return context;
            }).ToArray();

            var results = await Task.WhenAll(attempts.Select(context =>
                Task.Run(() => Throttle(context, new FixedClock(Noon))
                    .TryRecordAsync(email, source, CancellationToken.None))));

            Assert.Equal(SignInThrottle.MaxPerEmail, results.Count(wait => wait is null));

            // And the table agrees: nothing slipped in that the decision refused.
            await using var fresh = database.NewContext();
            Assert.Equal(
                SignInThrottle.MaxPerEmail,
                await fresh.SignInAttempts.CountAsync(a => a.NormalizedEmail == email));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    // ---- T118: Retry-After reads the row that actually frees a slot -------------------

    [Fact]
    public async Task Retry_After_expires_with_the_oldest_attempt_not_the_newest()
    {
        // Finding N1. A bucket of ten has room again when the tenth-most-recent attempt
        // falls out of the window — which, in a bucket that is exactly full, is the OLDEST
        // row. Reading the tenth-OLDEST instead pointed at the newest row and told a member
        // to wait almost a full fifteen minutes for a slot opening in seconds.
        await using var context = database.NewContext();
        var clock = new FixedClock(Noon);
        var throttle = Throttle(context, clock);
        var email = NewEmail();
        var source = NewSource();

        // Ten failures, one a minute. The first is nine minutes older than the last.
        for (var attempt = 0; attempt < SignInThrottle.MaxPerEmail; attempt++)
        {
            clock.Now = Noon.AddMinutes(attempt);
            Assert.Null(await throttle.TryRecordAsync(email, source, CancellationToken.None));
        }

        clock.Now = Noon.AddMinutes(SignInThrottle.MaxPerEmail);
        var wait = await throttle.TryRecordAsync(email, source, CancellationToken.None);

        // The oldest attempt was at Noon and the window is fifteen minutes, so it leaves at
        // 12:15 — five minutes after the clock's 12:10. The tenth-OLDEST row, which is what
        // this read until N1, is the 12:09 one, and it would have answered fourteen: nearly
        // three times as long, for a slot that opens in five.
        Assert.NotNull(wait);
        Assert.Equal(TimeSpan.FromMinutes(5), wait!.Value);
    }

    [Fact]
    public async Task Retry_After_is_never_zero_or_negative()
    {
        // A Retry-After of 0 reads as "go ahead", which turns a refusal into an invitation
        // to retry in a loop.
        await using var context = database.NewContext();
        var clock = new FixedClock(Noon);
        var throttle = Throttle(context, clock);
        var email = NewEmail();
        var source = NewSource();

        for (var attempt = 0; attempt < SignInThrottle.MaxPerEmail; attempt++)
        {
            await throttle.TryRecordAsync(email, source, CancellationToken.None);
        }

        // Right at the instant the oldest attempt leaves the window.
        clock.Now = Noon.Add(SignInThrottle.Window);
        var wait = await throttle.TryRecordAsync(email, source, CancellationToken.None);

        // The oldest attempt is exactly at the window's edge, so it is still counted and
        // the honest answer to "how long until it leaves" is zero.
        Assert.NotNull(wait);
        Assert.True(wait!.Value >= TimeSpan.FromSeconds(1), $"Retry-After was {wait.Value}.");
    }

    private async Task<int> CountAsync(string normalizedEmail)
    {
        await using var context = database.NewContext();
        return await context.SignInAttempts.CountAsync(a => a.NormalizedEmail == normalizedEmail);
    }
}
