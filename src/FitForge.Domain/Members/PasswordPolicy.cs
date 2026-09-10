namespace FitForge.Domain.Members;

/// <summary>
/// The rule a password must satisfy, and the only place it is decided.
/// </summary>
/// <remarks>
/// <para>
/// FR-002: enforced server-side, minimum ten characters, and <b>no composition rules</b>.
/// That prohibition is a requirement rather than an oversight — mixed-case-and-a-symbol
/// rules measurably push people toward <c>Password1!</c>, and length is the property that
/// survives contact with real users.
/// </para>
/// <para>
/// Lives in <c>FitForge.Domain</c>, which references no package (ADR-001 §4.2), so this is
/// a pure function over two strings and is unit-tested with no host. The browser may
/// mirror it as a courtesy; invariant 7 means the browser's copy is never the enforcement.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// FR-002's floor. Ten, not eight: the requirement says "at least 10".
    /// </summary>
    public const int MinimumLength = 10;

    /// <summary>
    /// An upper bound, and it is a denial-of-service control rather than a security rule.
    /// </summary>
    /// <remarks>
    /// Verification is a deliberate ~200 ms stretch (<c>plan.md</c> D2). Without a cap, a
    /// ten-megabyte request body buys an attacker that CPU time per request. Rejecting
    /// before hashing is what makes the cap worth having; rejecting after would spend the
    /// time it exists to save.
    /// </remarks>
    public const int MaximumLength = 256;

    /// <summary>
    /// Checks a candidate password. <see cref="PasswordPolicyViolation.None"/> means it
    /// passes.
    /// </summary>
    /// <param name="password">The candidate, unhashed and untrimmed.</param>
    /// <param name="email">
    /// The address the password would protect. Compared case-insensitively.
    /// </param>
    /// <remarks>
    /// <para>
    /// The password is <b>not</b> trimmed. Leading and trailing spaces are characters a
    /// member deliberately chose, and silently removing them would mean the password they
    /// typed is not the password that was stored.
    /// </para>
    /// <para>
    /// Order matters: length is checked before the email comparison, so an over-long input
    /// is rejected on the cheapest possible test.
    /// </para>
    /// </remarks>
    public static PasswordPolicyViolation Check(string password, string email)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(email);

        if (password.Length < MinimumLength)
        {
            return PasswordPolicyViolation.TooShort;
        }

        if (password.Length > MaximumLength)
        {
            return PasswordPolicyViolation.TooLong;
        }

        // Not a composition rule, and worth the two lines: the address is the one string
        // an attacker already has. Normalized on both sides so that trailing whitespace or
        // a different capitalization does not slip the same value through.
        if (string.Equals(
                EmailAddress.Normalize(password),
                EmailAddress.Normalize(email),
                StringComparison.Ordinal))
        {
            return PasswordPolicyViolation.SameAsEmail;
        }

        return PasswordPolicyViolation.None;
    }
}

/// <summary>
/// Which rule a candidate password broke, if any.
/// </summary>
/// <remarks>
/// An enum rather than a message: the wording a member sees belongs to the layer that
/// speaks HTTP, and the domain names no status code and no copy (ADR-001 §4.4's reasoning,
/// applied to text instead of status).
/// </remarks>
public enum PasswordPolicyViolation
{
    None = 0,
    TooShort = 1,
    TooLong = 2,
    SameAsEmail = 3,
}
