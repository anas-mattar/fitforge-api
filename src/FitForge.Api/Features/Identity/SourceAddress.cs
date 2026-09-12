using System.Net;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// Decides who a request came from, for the sign-in throttle's per-source bucket
/// (<c>contracts/auth.md</c> §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a class and not a line in a handler.</b> The line it replaces was
/// <c>http.Request.Headers["X-Forwarded-For"].ToString()</c>, and it had two defects at
/// once (feature 002, finding F1). It trusted the header from any caller, so a direct
/// caller got a fresh bucket per request by varying one string. And when the header was
/// absent — which, until this phase, it always was, because the BFF never sent it — it
/// produced the empty string, which hashed to ONE bucket shared by every member in the
/// system. Thirty failed sign-ins from anywhere locked out everyone.
/// </para>
/// <para>
/// Both defects are the same mistake: treating "I do not know who this is" as if it were
/// an identity. So this type returns <see langword="null"/> for unknown, the throttle
/// declines to consult a per-source bucket it cannot key, and the per-email bucket —
/// which is never unknown — carries the defence on its own.
/// </para>
/// </remarks>
public sealed class SourceAddress(IOptions<SignInThrottleOptions> options)
{
    /// <summary>The header a trusted proxy names the caller in.</summary>
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>
    /// The caller's address, or <see langword="null"/> when it is not known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three outcomes, and the third is the one worth reading twice:
    /// </para>
    /// <list type="number">
    /// <item>The socket came from an address that is <b>not</b> a configured proxy. That
    /// caller speaks only for itself, so its own address is the answer and any header it
    /// set is ignored.</item>
    /// <item>The socket came from a configured proxy and the proxy named someone. That
    /// name is the answer.</item>
    /// <item>The socket came from a configured proxy and it named nobody, named something
    /// unparseable, or named another proxy. The answer is <b>unknown</b> — deliberately
    /// not the proxy's own address, because every member behind that proxy would then
    /// share one bucket, which is exactly the outage F1 describes.</item>
    /// </list>
    /// </remarks>
    public string? Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var peer = http.Connection.RemoteIpAddress;

        if (peer is null)
        {
            // No transport underneath — a test host, or an in-process call. Unknown is
            // honest; inventing a placeholder would create a shared bucket.
            return null;
        }

        if (!options.Value.TrustsProxy(peer))
        {
            return SignInThrottleOptions.Canonical(peer).ToString();
        }

        var forwarded = Forwarded(http);

        // A trusted proxy that forwarded another trusted proxy has told us nothing about
        // the caller: the chain is longer than the deployment expects, and using the inner
        // proxy's address would put every member behind it in one bucket.
        return forwarded is null || options.Value.TrustsProxy(forwarded)
            ? null
            : forwarded.ToString();
    }

    /// <summary>
    /// The caller as the nearest trusted proxy named them, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The LAST entry, not the first.</b> A proxy appends the address it saw, so the
    /// rightmost entry is the one the nearest proxy wrote and the leftmost is whatever the
    /// original caller chose to send. Reading the leftmost — the reflex, and what most
    /// "get the client IP" snippets do — reads attacker-controlled input.
    /// </para>
    /// <para>
    /// <b>The assumption this makes, stated so it can be checked.</b> Exactly one trusted
    /// hop is expected between the caller and this API: the BFF. A deployment that puts a
    /// second appending proxy in front of the BFF must collapse the header at the edge,
    /// because the rightmost entry would then be the inner proxy — and outcome 3 above
    /// turns that into "unknown" rather than into a shared bucket, so the failure is a
    /// weaker throttle and never an outage.
    /// </para>
    /// </remarks>
    private static IPAddress? Forwarded(HttpContext http)
    {
        var header = http.Request.Headers[ForwardedForHeader].ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        var last = header.Split(',')[^1].Trim();

        return TryParseEntry(last, out var address) ? address : null;
    }

    /// <summary>
    /// Parses one <c>X-Forwarded-For</c> entry, with or without the port some proxies add.
    /// </summary>
    private static bool TryParseEntry(string entry, out IPAddress? address)
    {
        address = null;

        if (entry.Length == 0)
        {
            return false;
        }

        // "[2001:db8::1]:4711" and "203.0.113.7:4711" both appear in the wild. A bare
        // IPv6 address has several colons and no brackets, so the port is only stripped
        // when the shape is unambiguous.
        if (entry[0] == '[')
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            entry = close > 0 ? entry[1..close] : entry;
        }
        else if (entry.Count(c => c == ':') == 1)
        {
            entry = entry[..entry.IndexOf(':', StringComparison.Ordinal)];
        }

        if (!IPAddress.TryParse(entry, out var parsed))
        {
            return false;
        }

        address = SignInThrottleOptions.Canonical(parsed);
        return true;
    }
}
