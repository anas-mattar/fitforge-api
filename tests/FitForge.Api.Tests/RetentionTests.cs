using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FitForge.Api.Features.Identity;
using FitForge.Api.Hosting.Retention;
using FitForge.Domain.Members;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FitForge.Api.Tests;

/// <summary>
/// T066–T068 — the purge, and the guard that outlives this feature.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class RetentionTests(SqlServerDatabase database)
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private RetentionRunner Runner(FitForgeDbContext context, TimeProvider clock) =>
        new(context, clock, NullLogger<RetentionRunner>.Instance);

    private async Task<long> SeedDeletedMemberAsync(DateTime? deletedAtUtc)
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
            IsDeleted = deletedAtUtc is not null,
            DeletedAtUtc = deletedAtUtc,
            DeletedBy = deletedAtUtc is null ? null : "test",
        };

        member.Profile = new Profile
        {
            CreatedAtUtc = Noon.UtcDateTime,
            CreatedBy = "test",
            IsDeleted = deletedAtUtc is not null,
            DeletedAtUtc = deletedAtUtc,
            DeletedBy = deletedAtUtc is null ? null : "test",
        };

        context.Members.Add(member);
        await context.SaveChangesAsync();

        context.Sessions.Add(new Session
        {
            MemberId = member.Id,
            TokenHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            CreatedAtUtc = Noon.UtcDateTime,
            ExpiresAtUtc = Noon.UtcDateTime.AddDays(14),
            LastSeenAtUtc = Noon.UtcDateTime,
        });
        await context.SaveChangesAsync();

        return member.Id;
    }

    // ---- T066 / D13-1: the guard --------------------------------------------------

    [Fact]
    public void Every_member_owned_entity_is_named_in_the_purge()
    {
        // plan.md D13-1. This test is not about today's schema — it is about the day
        // someone adds WorkoutSession, SetEntry or BodyMetric and does not think about
        // deletion. Invariant 10 promises a member their data becomes unrecoverable
        // after thirty days; an entity nobody added to the purge quietly breaks that
        // promise, and nothing else in the suite would notice.
        using var context = database.NewContext();

        var memberOwned = context.Model.GetEntityTypes()
            .Where(entity => entity.GetForeignKeys()
                .Any(fk => fk.PrincipalEntityType.ClrType == typeof(Member)))
            .Select(entity => entity.ClrType.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(memberOwned);

        foreach (var entity in memberOwned)
        {
            Assert.True(
                RetentionPolicy.Purged.Contains(entity),
                $"'{entity}' has a foreign key to Member but is not named in " +
                $"RetentionPolicy.Purged. Either the retention purge must remove it, or " +
                $"there must be a written reason it survives a member's erasure " +
                $"(training invariant 10).");
        }

        // And the member row itself.
        Assert.Contains(nameof(Member), RetentionPolicy.Purged);
    }

    [Fact]
    public void The_purge_set_names_nothing_that_does_not_exist()
    {
        // The other direction. A name left behind after an entity is renamed or removed
        // would make the test above pass for the wrong reason — it would be comparing
        // against a set that describes a schema nobody has any more.
        using var context = database.NewContext();

        var known = context.Model.GetEntityTypes()
            .Select(e => e.ClrType.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var purged in RetentionPolicy.Purged)
        {
            Assert.Contains(purged, known);
        }
    }

    // ---- T067: the window ----------------------------------------------------------

    [Fact]
    public async Task A_member_deleted_past_the_window_is_physically_removed()
    {
        var memberId = await SeedDeletedMemberAsync(Noon.UtcDateTime);
        var clock = new FixedClock(Noon + RetentionPolicy.Window + TimeSpan.FromDays(1));

        await using var context = database.NewContext();
        var result = await Runner(context, clock).RunAsync(CancellationToken.None);

        Assert.True(result.MembersRemoved >= 1);

        await using var fresh = database.NewContext();
        Assert.False(await fresh.Members.IgnoreQueryFilters().AnyAsync(m => m.Id == memberId));
    }

    [Fact]
    public async Task A_member_deleted_inside_the_window_is_left_alone()
    {
        // Twenty-nine days. The member can still be restored, which is what VI-027
        // promises them — "recoverable for 30 days".
        var memberId = await SeedDeletedMemberAsync(Noon.UtcDateTime);
        var clock = new FixedClock(Noon + RetentionPolicy.Window - TimeSpan.FromDays(1));

        await using var context = database.NewContext();
        await Runner(context, clock).RunAsync(CancellationToken.None);

        await using var fresh = database.NewContext();
        Assert.True(await fresh.Members.IgnoreQueryFilters().AnyAsync(m => m.Id == memberId));
    }

    [Fact]
    public async Task A_member_who_never_deleted_their_account_is_never_touched()
    {
        // The failure that would be catastrophic and silent: a purge that ignored
        // IsDeleted and worked off age alone would erase the product's oldest and most
        // loyal members first.
        var memberId = await SeedDeletedMemberAsync(deletedAtUtc: null);
        var clock = new FixedClock(Noon + TimeSpan.FromDays(3650));

        await using var context = database.NewContext();
        await Runner(context, clock).RunAsync(CancellationToken.None);

        await using var fresh = database.NewContext();
        Assert.True(await fresh.Members.IgnoreQueryFilters().AnyAsync(m => m.Id == memberId));
    }

    // ---- T068: nothing is orphaned --------------------------------------------------

    [Fact]
    public async Task The_purge_removes_the_profile_and_the_sessions_too()
    {
        var memberId = await SeedDeletedMemberAsync(Noon.UtcDateTime);
        var clock = new FixedClock(Noon + RetentionPolicy.Window + TimeSpan.FromDays(1));

        await using var context = database.NewContext();
        await Runner(context, clock).RunAsync(CancellationToken.None);

        await using var fresh = database.NewContext();

        Assert.False(await fresh.Profiles.IgnoreQueryFilters().AnyAsync(p => p.MemberId == memberId));
        Assert.False(await fresh.Sessions.AnyAsync(s => s.MemberId == memberId));
    }

    [Fact]
    public async Task Sign_in_attempts_past_the_throttle_window_are_pruned_and_recent_ones_kept()
    {
        var clock = new FixedClock(Noon);

        await using (var seed = database.NewContext())
        {
            seed.SignInAttempts.Add(new SignInAttempt
            {
                NormalizedEmail = "OLD@EXAMPLE.COM",
                SourceHash = new byte[32],
                AttemptedAtUtc = Noon.UtcDateTime - SignInThrottle.Window - TimeSpan.FromMinutes(1),
            });
            seed.SignInAttempts.Add(new SignInAttempt
            {
                NormalizedEmail = "RECENT@EXAMPLE.COM",
                SourceHash = new byte[32],
                AttemptedAtUtc = Noon.UtcDateTime - TimeSpan.FromMinutes(1),
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.NewContext();
        await Runner(context, clock).RunAsync(CancellationToken.None);

        await using var fresh = database.NewContext();

        Assert.False(await fresh.SignInAttempts.AnyAsync(a => a.NormalizedEmail == "OLD@EXAMPLE.COM"));

        // The recent one survives — pruning the live window would reset every throttle
        // bucket on each pass and quietly disable FR-016.
        Assert.True(await fresh.SignInAttempts.AnyAsync(a => a.NormalizedEmail == "RECENT@EXAMPLE.COM"));
    }

    [Fact]
    public async Task A_pass_with_nothing_to_do_removes_nothing()
    {
        var clock = new FixedClock(Noon);

        await using var context = database.NewContext();
        var result = await Runner(context, clock).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.MembersRemoved);
    }
}
