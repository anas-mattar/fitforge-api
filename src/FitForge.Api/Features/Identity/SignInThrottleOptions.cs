using System.Net;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// Configuration for the sign-in throttle, validated at startup.
/// </summary>
/// <remarks>
/// Two values. One is a secret — the salt that makes a stored source address
/// irreversible. The other is not a secret at all but is just as load-bearing: the list of
/// hosts whose <c>X-Forwarded-For</c> this API believes. Both are names in
/// <c>appsettings.json</c> with empty values; both fail startup when unset, the same shape
/// as <c>FitForge.Infrastructure.DatabaseOptions</c>.
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
    /// What a developer sees when no proxy is trusted.
    /// </summary>
    /// <remarks>
    /// The message spends its length on the failure mode rather than on the syntax,
    /// because the syntax is guessable and the failure mode is not: with nothing
    /// configured, every request's source is the BFF's own address, every member shares
    /// one bucket of thirty, and thirty failed sign-ins lock the whole product out. That
    /// is feature 002 finding F1, and it is the reason this setting is required rather
    /// than defaulted.
    /// </remarks>
    public const string MissingTrustedProxiesMessage =
        "No trusted proxies. Set 'Security:TrustedProxies' to the address of every host " +
        "allowed to tell this API who it is talking to — the BFF, and any reverse proxy in " +
        "front of it — as the environment variable Security__TrustedProxies__0 (…__1, …), " +
        "or in appsettings for a local run. Entries are IP addresses ('10.1.2.3') or CIDR " +
        "ranges ('10.1.0.0/16'). It has no default on purpose: with an empty list the API " +
        "never learns a caller's address, the sign-in throttle counts every member against " +
        "ONE per-source bucket of " + nameof(SignInThrottle.MaxPerSource) + ", and that many " +
        "failed sign-ins locks out every member at once (feature 002, finding F1).";

    /// <summary>What a developer sees when an entry is not an address or a range.</summary>
    public const string MalformedTrustedProxyMessage =
        "'Security:TrustedProxies' has an entry that is neither an IP address nor a CIDR " +
        "range. A typo here does not fail loudly at request time — it silently stops " +
        "trusting the proxy that entry was meant to name, which puts the throttle back into " +
        "the one-bucket state finding F1 describes. Fix the entry.";

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

    /// <summary>
    /// Every host allowed to tell this API who it is talking to, as an IP address or a
    /// CIDR range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a trust boundary, not a convenience.</b> <c>X-Forwarded-For</c> is a
    /// request header: anyone who can reach the API can set it to anything. Honouring it
    /// unconditionally hands every caller a fresh throttle bucket per request by varying
    /// one string, which is a throttle that counts but never refuses.
    /// </para>
    /// <para>
    /// So the header is read only when the socket came from an address in this list, and
    /// otherwise the address the socket came from is used instead — the one thing a caller
    /// cannot forge.
    /// </para>
    /// </remarks>
    public IList<string> TrustedProxies { get; set; } = [];

    private IPAddress[]? _addresses;
    private IPNetwork[]? _networks;

    /// <summary>
    /// Whether <paramref name="peer"/> is a host this API lets speak for other callers.
    /// </summary>
    public bool TrustsProxy(IPAddress peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        Parse();

        // Compared in one canonical form. A proxy configured as 10.1.2.3 connecting over a
        // dual-stack socket arrives as ::ffff:10.1.2.3, and an entry that silently fails
        // to match is this setting's whole failure mode.
        var candidate = Canonical(peer);

        return Array.Exists(_addresses!, a => a.Equals(candidate))
            || Array.Exists(_networks!, n => n.Contains(candidate));
    }

    /// <summary>Whether every entry is an address or a range.</summary>
    /// <remarks>
    /// Used by startup validation. A malformed entry is a configuration error and belongs
    /// at startup with a message, not at request time as a proxy that quietly stopped
    /// being trusted.
    /// </remarks>
    public bool EntriesAreWellFormed() =>
        TrustedProxies.All(entry =>
            entry.Contains('/', StringComparison.Ordinal)
                ? IPNetwork.TryParse(entry, out _)
                : IPAddress.TryParse(entry, out _));

    /// <summary>IPv4-mapped IPv6 addresses collapse to their IPv4 form.</summary>
    internal static IPAddress Canonical(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private void Parse()
    {
        if (_addresses is not null)
        {
            return;
        }

        // Parsed once and cached. IOptions<T> hands out one instance, and re-parsing a
        // handful of strings on every sign-in would be work done for no reason.
        _networks = [.. TrustedProxies
            .Where(entry => entry.Contains('/', StringComparison.Ordinal))
            .Select(entry => IPNetwork.TryParse(entry, out var network) ? network : (IPNetwork?)null)
            .OfType<IPNetwork>()];

        _addresses = [.. TrustedProxies
            .Where(entry => !entry.Contains('/', StringComparison.Ordinal))
            .Select(entry => IPAddress.TryParse(entry, out var address) ? Canonical(address) : null)
            .OfType<IPAddress>()];
    }
}
