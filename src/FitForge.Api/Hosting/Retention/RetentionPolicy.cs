using FitForge.Domain.Members;

namespace FitForge.Api.Hosting.Retention;

/// <summary>
/// How long a deleted member's data is recoverable, and what "their data" means.
/// </summary>
/// <remarks>
/// Training invariant 10: account deletion MUST soft-delete the member and their training
/// data <b>and MUST make it unrecoverable after the stated retention window</b>. The
/// second half is the one a soft delete cannot do, and it is why this exists in feature
/// 002 rather than being deferred to whenever deletion first matters.
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>
    /// Thirty days, and it is not a configurable convenience.
    /// </summary>
    /// <remarks>
    /// VI-027 states this number <b>to the member</b>, in the product: "recoverable for 30
    /// days, then permanently removed". The number therefore lives in one place and the
    /// two move together or neither does. If it ever becomes configurable, the copy the
    /// member reads has to be generated from the same value, not written beside it.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    /// <summary>
    /// Every entity type the purge removes, named explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared as data rather than left implicit in the purge's code so that
    /// <c>RetentionCompletenessTests</c> can compare it against the model. That test is
    /// <c>plan.md</c> D13-1, and it is the one that outlives this feature: a later
    /// feature adding a member-owned table without extending this set fails the gate
    /// instead of silently orphaning personal data past the window a member was promised.
    /// </para>
    /// <para>
    /// <see cref="Member"/> is in the set because the member row is itself removed last.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlySet<string> Purged = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Profile),
        nameof(Session),
        nameof(Member),
    };
}
