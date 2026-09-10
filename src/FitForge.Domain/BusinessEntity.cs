namespace FitForge.Domain;

/// <summary>
/// Base type for every entity FitForge persists as business data: the project primary-key
/// standard and the audit and soft-delete fields, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/rulebooks/database-rules.md</c> requires these fields on every business entity,
/// and training invariant 8 makes the identity half of it constitutional. Declaring them
/// once means a new entity gets them by inheriting rather than by the author remembering —
/// the same reasoning as ADR-001 §4.2, applied a level down.
/// </para>
/// <para>
/// Not every persisted row is a business entity. <c>Session</c> and <c>SignInAttempt</c>
/// (feature 002 phase 3) deliberately derive from nothing: they have no external identity
/// and no audit story, and <c>specs/002-identity-member-profile/data-model.md</c> records
/// the three deviations and the reasoning. That is why this base exists as an opt-in type
/// rather than as a rule the context applies to everything it sees.
/// </para>
/// <para>
/// Every instant here is UTC (invariant 4). The type is <see cref="DateTime"/> rather than
/// <see cref="DateTimeOffset"/> because the column is <c>DATETIME2(3)</c> and there is no
/// offset to carry: a value that is not UTC is a defect, not a variant.
/// </para>
/// </remarks>
public abstract class BusinessEntity
{
    /// <summary>
    /// The internal key. MUST NOT appear in a URL, payload, or log line (invariant 8).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The only identifier that may leave the API.
    /// </summary>
    /// <remarks>
    /// Version 7 rather than version 4: it is time-ordered, so the unique index on it does
    /// not fragment the way random GUIDs do, while staying opaque and non-enumerable. The
    /// default is assigned here so that no creation path can forget it.
    /// </remarks>
    public Guid PublicId { get; set; } = Guid.CreateVersion7();

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// The <c>PublicId</c> of the member who caused the row to be written, or the literal
    /// <c>system</c> for rows written by a background service. Never an internal key.
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    /// <summary>
    /// Soft-delete marker. A global query filter excludes these rows, so a query has to opt
    /// in to seeing them rather than opt out.
    /// </summary>
    public bool IsDeleted { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    public string? DeletedBy { get; set; }
}
