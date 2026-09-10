namespace FitForge.Api.Features.Identity;

/// <summary>
/// Configuration for the sign-in throttle, validated at startup.
/// </summary>
/// <remarks>
/// One value, and it is a secret: the salt that makes a stored source address
/// irreversible. The name is in <c>appsettings.json</c> with an empty value; the value
/// comes from the environment (constitution VI), same shape as
/// <c>FitForge.Infrastructure.DatabaseOptions</c>.
/// </remarks>
public sealed class SignInThrottleOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Security";

    /// <summary>
    /// What a developer sees when they run the API without configuring the salt.
    /// </summary>
    /// <remarks>
    /// Names the setting in both forms they might type, for the same reason
    /// <c>DatabaseOptions</c> does: failing at startup only saves someone an afternoon if
    /// the message says what to set.
    /// </remarks>
    public const string MissingSaltMessage =
        "No source-address salt. Set 'Security:SourceAddressSalt' — as the environment " +
        "variable Security__SourceAddressSalt, or in the Development environment with " +
        "'dotnet user-secrets set \"Security:SourceAddressSalt\" \"...\" --project src/FitForge.Api'. " +
        "It is deliberately empty in appsettings.json: the name belongs in source, the value " +
        "does not. Without it the stored hash of an IPv4 address is reversible by brute force " +
        "in seconds — there are only four billion of them — which would make the throttle's " +
        "counter a table of members' home addresses (training invariant 10).";

    /// <summary>
    /// Salts the hash of a source address before it is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the address is hashed at all.</b> The throttle only ever needs equality: it
    /// counts attempts from the same place and never displays, reverses or reports one.
    /// Invariant 10 keeps the collected data minimal, and an address sitting beside an
    /// email address is the kind of row that turns a small breach into a large one.
    /// </para>
    /// <para>
    /// <b>Why unsalted is not enough.</b> The IPv4 space is small enough to enumerate
    /// exhaustively against a fast hash, so an unsalted SHA-256 of an address is a
    /// reversible encoding wearing a hash's clothes.
    /// </para>
    /// <para>
    /// Rotating it resets the source buckets, which is harmless: the window is fifteen
    /// minutes.
    /// </para>
    /// </remarks>
    public string SourceAddressSalt { get; set; } = string.Empty;
}
