namespace FitForge.Domain.Members;

/// <summary>
/// A person's login identity and account. FitForge's first entity, and the one every other
/// entity will hang off.
/// </summary>
/// <remarks>
/// <para>
/// Training invariant 2 — every record belongs to exactly one member and never leaves them
/// — is the rule this type exists to make possible. The API surface enforces it by shape:
/// every member-owned path is <c>/me</c>-shaped and takes no member identifier at all
/// (<c>plan.md</c> D7).
/// </para>
/// <para>
/// Behaviour arrives with the phases that need it: password verification and rehashing in
/// phase 2, sessions in phase 3, preference changes and deletion in phase 5. This phase
/// establishes the shape and the schema, so the properties are settable and the invariants
/// that a constraint can express live in <c>MemberConfiguration</c>.
/// </para>
/// </remarks>
public class Member : BusinessEntity
{
    /// <summary>
    /// The address as the member typed it, trimmed. Shown back to them; never compared.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The comparison form, from <see cref="EmailAddress.Normalize"/>. Unique.
    /// </summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>
    /// A salted, deliberately slow one-way hash. Never the password, in any recoverable
    /// form, anywhere (FR-003).
    /// </summary>
    /// <remarks>
    /// The algorithm and its parameters are <c>plan.md</c> D2 and are applied in phase 2.
    /// The column is sized for a versioned composite, so moving to a different algorithm
    /// later is a rehash on sign-in rather than a migration.
    /// </remarks>
    public string PasswordHash { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Display units. Changing this rewrites nothing (invariant 4, FR-011).
    /// </summary>
    public UnitPreference Units { get; set; } = UnitPreference.Metric;

    /// <summary>
    /// An IANA time-zone identifier — the basis for every day and week boundary the product
    /// computes (FR-012).
    /// </summary>
    /// <remarks>
    /// Validated against the host's zone database before it is stored (<c>plan.md</c> D10);
    /// an identifier the host cannot resolve is refused rather than silently replaced with
    /// UTC. Nothing in feature 002 computes a boundary — this exists so that the feature
    /// which does inherits a validated value instead of free text.
    /// </remarks>
    public string TimeZone { get; set; } = "UTC";

    public Goal Goal { get; set; } = Goal.Hypertrophy;

    public ExperienceLevel Experience { get; set; } = ExperienceLevel.Intermediate;

    /// <summary>
    /// The member's descriptive data. Created with the member, so no read path has to
    /// handle its absence.
    /// </summary>
    public Profile? Profile { get; set; }
}
