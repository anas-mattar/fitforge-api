namespace FitForge.Domain.Members;

/// <summary>
/// Email normalization — the one place FitForge decides that two spellings are the same
/// login identity.
/// </summary>
/// <remarks>
/// <para>
/// FR-001 requires the email to be unique case-insensitively and after trimming. Both
/// halves of that are decided here, and <c>UQ_Member_NormalizedEmail</c> enforces the
/// result. Doing it in code and *also* in a constraint is deliberate: application-only
/// enforcement is one forgotten code path away from two accounts on one address
/// (<c>docs/rulebooks/database-rules.md</c>, "Constraints Mirror Invariants").
/// </para>
/// <para>
/// Not left to the database collation. SQL Server's default collation happens to be
/// case-insensitive, which would make FR-001 hold by accident on a server setting anyone
/// can change — and it would not trim at all.
/// </para>
/// </remarks>
public static class EmailAddress
{
    /// <summary>
    /// The comparison form of an address: trimmed, then upper-cased invariantly.
    /// </summary>
    /// <remarks>
    /// Upper rather than lower. Lower-casing has locale traps — the Turkish dotless i is
    /// the standard example — that upper-casing does not, and ASP.NET Core Identity
    /// normalizes the same direction for the same reason.
    /// </remarks>
    public static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        return email.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// The stored, member-facing form: trimmed, but with the member's own capitalization
    /// left alone.
    /// </summary>
    /// <remarks>
    /// A member who writes <c>Anas.M@example.com</c> sees it back that way. Only
    /// <see cref="Normalize"/>'s output is ever compared.
    /// </remarks>
    public static string ForDisplay(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        return email.Trim();
    }
}
