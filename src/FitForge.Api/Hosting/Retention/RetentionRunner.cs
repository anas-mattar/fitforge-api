using FitForge.Api.Features.Identity;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Hosting.Retention;

/// <summary>
/// One pass of the retention purge. Physically removes members whose deletion is older
/// than <see cref="RetentionPolicy.Window"/>, and prunes the throttle's counter.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>RetentionService</c>, which only decides <i>when</i> this runs. A
/// hosted service that also contains the work can only be tested by starting a host and
/// waiting, and a test that waits a day is a test nobody runs.
/// </para>
/// <para>
/// <b>This is the one irreversible act in feature 002.</b> Everything else soft-deletes,
/// revokes or updates. <c>rollback.md</c> says so explicitly: if a purge has already run,
/// no revert brings those rows back, and that is invariant 10 working rather than a
/// defect.
/// </para>
/// </remarks>
public sealed class RetentionRunner(
    FitForgeDbContext db,
    TimeProvider clock,
    ILogger<RetentionRunner> logger)
{
    /// <summary>
    /// Runs one pass and reports what it removed.
    /// </summary>
    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now - RetentionPolicy.Window;

        // IgnoreQueryFilters, because every member this method exists for is soft-deleted
        // and therefore invisible to an ordinary query. Without it the purge would find
        // nothing, always, and look like it was working.
        var due = await db.Members
            .IgnoreQueryFilters()
            .Where(m => m.IsDeleted && m.DeletedAtUtc != null && m.DeletedAtUtc < cutoff)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        var membersRemoved = 0;

        foreach (var memberId in due)
        {
            // One transaction per member rather than one for the batch: a failure part
            // way through leaves earlier members fully removed and later ones untouched,
            // never a member half-removed with orphaned rows referencing nothing.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Children first. The foreign keys are RESTRICT, deliberately
            // (database-rules.md), so removing the member first would fail rather than
            // cascade — which is the protection working, not an obstacle to route around.
            await db.Sessions
                .Where(s => s.MemberId == memberId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.Profiles
                .IgnoreQueryFilters()
                .Where(p => p.MemberId == memberId)
                .ExecuteDeleteAsync(cancellationToken);

            await db.Members
                .IgnoreQueryFilters()
                .Where(m => m.Id == memberId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            membersRemoved++;
        }

        // The throttle's counter is a counter, not a log. Rows past their window have no
        // remaining purpose, and keeping them would be keeping personal data — a hashed
        // source address is still a record that someone tried to sign in (invariant 10's
        // "minimal").
        var attemptsPruned = await db.SignInAttempts
            .Where(a => a.AttemptedAtUtc < now - SignInThrottle.Window)
            .ExecuteDeleteAsync(cancellationToken);

        if (membersRemoved > 0 || attemptsPruned > 0)
        {
            // Counts only. No email, no address, no PublicId, no internal Id (plan.md
            // D12). "Which member was erased" is precisely the fact erasure is supposed
            // to destroy, so writing it to a log would undo the work in the same breath.
            logger.LogInformation(
                "Retention pass removed {MemberCount} member(s) past the {WindowDays}-day window and pruned {AttemptCount} sign-in attempt(s).",
                membersRemoved,
                RetentionPolicy.Window.TotalDays,
                attemptsPruned);
        }

        return new RetentionResult(membersRemoved, attemptsPruned);
    }
}

/// <summary>What one retention pass removed.</summary>
/// <param name="MembersRemoved">Members physically removed, with everything they owned.</param>
/// <param name="AttemptsPruned">Sign-in attempt rows past the throttle's window.</param>
public readonly record struct RetentionResult(int MembersRemoved, int AttemptsPruned);
