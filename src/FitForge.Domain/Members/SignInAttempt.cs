namespace FitForge.Domain.Members;

/// <summary>
/// One failed sign-in, counted toward the throttle (FR-016, <c>contracts/auth.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// Rows are written for addresses that <b>do not exist</b>, identically to ones that do.
/// That is the whole point: a throttle that only counted real accounts would make
/// 429-versus-401 the existence oracle the decoy hash spends CPU to close.
/// </para>
/// <para>
/// A counter, not a log. Rows older than the window are removed by the retention service
/// (phase 6) — keeping them would be keeping personal data past its usefulness
/// (invariant 10).
/// </para>
/// </remarks>
public class SignInAttempt
{
    public long Id { get; set; }

    /// <summary>
    /// The submitted address in comparison form, whether or not a member has it.
    /// </summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the source address plus a server-side salt.
    /// </summary>
    /// <remarks>
    /// Hashed rather than stored in the clear. An address is personal data under
    /// invariant 10's "minimal", the throttle only ever needs equality, and a table of
    /// members' home addresses beside their email is the kind of row that turns a small
    /// breach into a large one. The salt lives in configuration, never in source.
    /// </remarks>
    public byte[] SourceHash { get; set; } = [];

    public DateTime AttemptedAtUtc { get; set; }
}
