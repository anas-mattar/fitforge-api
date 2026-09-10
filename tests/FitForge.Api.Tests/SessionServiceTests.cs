using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FitForge.Api.Features.Identity;
using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Tests;

/// <summary>
/// T105–T107 (were T029–T031), against a real SQL Server — <c>tasks.md</c> A4.
/// </summary>
/// <remarks>
/// Session resolution is the mechanism training invariant 2 rests on: every read path
/// filters by the authenticated member, and this is where "the authenticated member"
/// comes from. It is tested against the engine that ships because a unique index and a
/// query filter behave like themselves and not like an approximation.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class SessionServiceTests(SqlServerDatabase database)
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<Member> SeedMemberAsync(bool softDeleted = false)
    {
        await using var context = database.NewContext();

        var email = $"member-{Guid.NewGuid():N}@example.com";
        var member = new Member
        {
            Email = email,
            NormalizedEmail = EmailAddress.Normalize(email),
            PasswordHash = "not-used-here",
            DisplayName = "Test Member",
            CreatedAtUtc = Noon.UtcDateTime,
            CreatedBy = "test",
            IsDeleted = softDeleted,
            DeletedAtUtc = softDeleted ? Noon.UtcDateTime : null,
            DeletedBy = softDeleted ? "test" : null,
        };

        context.Members.Add(member);
        await context.SaveChangesAsync();

        return member;
    }

    // ---- T105: one answer for four failures -------------------------------------

    [Fact]
    public async Task A_live_session_resolves_to_its_member()
    {
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);

        var token = await sessions.IssueAsync(member, CancellationToken.None);
        var resolved = await sessions.ResolveAsync(token, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(member.Id, resolved!.Member.Id);
        Assert.Equal(Noon.UtcDateTime + SessionService.Lifetime, resolved.ExpiresAtUtc);
    }

    [Fact]
    public async Task An_unknown_token_resolves_to_nothing()
    {
        await using var context = database.NewContext();
        var sessions = new SessionService(context, new FixedClock(Noon));

        var invented = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(await sessions.ResolveAsync(invented, CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_session_resolves_to_nothing()
    {
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        // One second past the lifetime. The boundary matters: a session must not outlive
        // its own expiry by a rounding error.
        clock.Now = Noon + SessionService.Lifetime + TimeSpan.FromSeconds(1);

        Assert.Null(await sessions.ResolveAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_session_resolves_to_nothing_even_before_it_expires()
    {
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        await sessions.RevokeAsync(token, CancellationToken.None);

        // Revocation is what FR-006 and FR-013 need and what a self-contained token could
        // not give: the session is dead thirteen days before it would have expired.
        Assert.Null(await sessions.ResolveAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_whose_member_is_soft_deleted_resolves_to_nothing()
    {
        // The fourth failure, and the one with no branch behind it: Members carries a
        // global query filter, so a soft-deleted member is not there to join to. Deleting
        // the check is impossible because there is no check.
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        await using (var other = database.NewContext())
        {
            var row = await other.Members.SingleAsync(m => m.Id == member.Id);
            row.IsDeleted = true;
            row.DeletedAtUtc = Noon.UtcDateTime;
            row.DeletedBy = "test";
            await other.SaveChangesAsync();
        }

        await using var fresh = database.NewContext();
        var freshSessions = new SessionService(fresh, clock);

        Assert.Null(await freshSessions.ResolveAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_is_idempotent_and_silent()
    {
        // FR-006: a second sign-out with the same token is still a no-op, so a caller
        // learns nothing from calling twice.
        var member = await SeedMemberAsync();

        await using var context = database.NewContext();
        var sessions = new SessionService(context, new FixedClock(Noon));
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        await sessions.RevokeAsync(token, CancellationToken.None);
        await sessions.RevokeAsync(token, CancellationToken.None);
        await sessions.RevokeAsync("a-token-that-never-existed", CancellationToken.None);

        Assert.Null(await sessions.ResolveAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_all_but_one_leaves_exactly_the_presented_session_alive()
    {
        // FR-013 after a password change: every other session dies, the tab in front of
        // the member survives.
        var member = await SeedMemberAsync();

        await using var context = database.NewContext();
        var sessions = new SessionService(context, new FixedClock(Noon));

        var keep = await sessions.IssueAsync(member, CancellationToken.None);
        var other1 = await sessions.IssueAsync(member, CancellationToken.None);
        var other2 = await sessions.IssueAsync(member, CancellationToken.None);

        await sessions.RevokeAllExceptAsync(member.Id, keep, CancellationToken.None);

        await using var fresh = database.NewContext();
        var freshSessions = new SessionService(fresh, new FixedClock(Noon));

        Assert.NotNull(await freshSessions.ResolveAsync(keep, CancellationToken.None));
        Assert.Null(await freshSessions.ResolveAsync(other1, CancellationToken.None));
        Assert.Null(await freshSessions.ResolveAsync(other2, CancellationToken.None));
    }

    // ---- T106: the token is nowhere in the database ------------------------------

    [Fact]
    public async Task The_token_appears_in_no_column_of_the_stored_row()
    {
        var member = await SeedMemberAsync();

        await using var context = database.NewContext();
        var sessions = new SessionService(context, new FixedClock(Noon));
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        await using var fresh = database.NewContext();
        var stored = await fresh.Sessions
            .Where(s => s.MemberId == member.Id)
            .OrderByDescending(s => s.Id)
            .FirstAsync();

        // The stored value is the hash, and the hash is not the token.
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), stored.TokenHash);
        Assert.NotEqual(Encoding.UTF8.GetBytes(token), stored.TokenHash);

        // And nothing else on the row is carrying it either — the assertion is about the
        // ROW, not about the one column we happen to be thinking about. A future column
        // that cached the token for convenience fails here.
        var asText = string.Join(
            '|',
            stored.Id,
            stored.MemberId,
            Convert.ToBase64String(stored.TokenHash),
            stored.CreatedAtUtc,
            stored.ExpiresAtUtc,
            stored.RevokedAtUtc,
            stored.LastSeenAtUtc);

        Assert.DoesNotContain(token, asText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_sessions_never_share_a_token()
    {
        var member = await SeedMemberAsync();

        await using var context = database.NewContext();
        var sessions = new SessionService(context, new FixedClock(Noon));

        var first = await sessions.IssueAsync(member, CancellationToken.None);
        var second = await sessions.IssueAsync(member, CancellationToken.None);

        Assert.NotEqual(first, second);

        // 43 characters of base64url — 256 bits. A shorter token would still pass every
        // other test in this file and would still be guessable.
        Assert.Equal(43, first.Length);
    }

    // ---- T107: the expiry slides, but not on every request -----------------------

    [Fact]
    public async Task Resolving_within_the_hour_does_not_move_the_expiry()
    {
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        var original = Noon.UtcDateTime + SessionService.Lifetime;

        clock.Now = Noon + TimeSpan.FromMinutes(59);
        var resolved = await sessions.ResolveAsync(token, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(original, resolved!.ExpiresAtUtc);
    }

    [Fact]
    public async Task Resolving_after_the_hour_slides_the_expiry_forward_and_persists_it()
    {
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        clock.Now = Noon + TimeSpan.FromHours(1);
        var resolved = await sessions.ResolveAsync(token, CancellationToken.None);

        var expected = clock.Now.UtcDateTime + SessionService.Lifetime;
        Assert.NotNull(resolved);
        Assert.Equal(expected, resolved!.ExpiresAtUtc);

        // Persisted, not just returned. A slide that lived only in the response would
        // expire the member mid-use fourteen days later, which is the bug this assertion
        // exists to prevent.
        await using var fresh = database.NewContext();
        var stored = await fresh.Sessions.SingleAsync(s => s.MemberId == member.Id);

        Assert.Equal(expected, stored.ExpiresAtUtc);
        Assert.Equal(clock.Now.UtcDateTime, stored.LastSeenAtUtc);
    }

    [Fact]
    public async Task A_long_lived_session_keeps_sliding_and_never_expires_under_use()
    {
        // The property that matters to a member: someone who opens FitForge every day
        // stays signed in. Fifteen days of hourly use, and the session is still alive
        // past the point a non-sliding one would have died.
        var member = await SeedMemberAsync();
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var sessions = new SessionService(context, clock);
        var token = await sessions.IssueAsync(member, CancellationToken.None);

        for (var day = 1; day <= 15; day++)
        {
            clock.Now = Noon + TimeSpan.FromDays(day);
            Assert.NotNull(await sessions.ResolveAsync(token, CancellationToken.None));
        }
    }
}
