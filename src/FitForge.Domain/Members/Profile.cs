namespace FitForge.Domain.Members;

/// <summary>
/// Slow-changing descriptive data belonging to exactly one member.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Member"/> rather than more columns on it: <c>Member</c> is read
/// on every authenticated request when a session resolves, while this is read on one
/// screen. Keeping them apart keeps the hot row narrow.
/// </para>
/// <para>
/// <b>Body weight is deliberately not here.</b> It is a time series — <c>BodyMetric</c>,
/// feature 010 — and a single mutable column would make invariant 1's "history is not
/// rewritten" impossible to state, let alone enforce.
/// </para>
/// <para>
/// Every field is optional. A member who gives nothing still has a row, so the read path
/// has one shape instead of two.
/// </para>
/// </remarks>
public class Profile : BusinessEntity
{
    /// <summary>
    /// The owning member's internal key. The restrict foreign key on this column is where
    /// invariant 2 becomes a schema fact rather than a convention.
    /// </summary>
    public long MemberId { get; set; }

    public Member? Member { get; set; }

    /// <summary>
    /// Year only — enough for age-relative training norms, and less than a birth date.
    /// Invariant 10 keeps the collected set minimal.
    /// </summary>
    public short? BirthYear { get; set; }

    public Sex? Sex { get; set; }

    /// <summary>
    /// Height in centimetres. Canonical storage, converted only for display (invariant 4).
    /// </summary>
    /// <remarks>
    /// <see cref="decimal"/>, never a floating-point type: <c>database-rules.md</c> forbids
    /// float for any member-visible number, because 167.5 is not what comes back out.
    /// </remarks>
    public decimal? HeightCm { get; set; }
}
