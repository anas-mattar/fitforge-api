namespace FitForge.Domain;

/// <summary>
/// Base type for every failure the domain itself can express.
/// </summary>
/// <remarks>
/// The domain never names an HTTP status code. Mapping these to responses is the host's
/// job and happens in exactly one place — FitForge.Api's Hosting/DomainExceptionHandler.
/// Keeping the two apart is what lets the domain be reused by a worker or a test harness
/// that has no notion of HTTP.
/// </remarks>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A resource the caller named does not exist, or does not belong to them.
/// </summary>
/// <remarks>
/// Training invariant 2 requires that one member can never observe another member's data.
/// "Exists but is not yours" and "does not exist" MUST therefore produce the same failure:
/// a distinguishable "forbidden" would let a caller probe for the existence of other
/// members' sessions. Both cases throw this.
/// </remarks>
public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message)
        : base(message)
    {
    }

    public static NotFoundException For(string resource, object id) =>
        new($"{resource} '{id}' was not found.");
}

/// <summary>
/// The request was understood but would break a domain rule — an invariant, a state
/// machine transition, or a validation rule the domain owns.
/// </summary>
public sealed class DomainRuleViolationException : DomainException
{
    public DomainRuleViolationException(string message)
        : base(message)
    {
    }
}
