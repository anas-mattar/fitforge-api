using System.Linq;
using System.Reflection;
using FitForge.Domain;

namespace FitForge.Domain.Tests;

public class DomainExceptionTests
{
    [Fact]
    public void Both_domain_failures_derive_from_the_single_base_type()
    {
        // The host maps on DomainException, so a future failure type that forgets to
        // derive from it would silently become a 500 instead of its intended status.
        Assert.IsAssignableFrom<DomainException>(new NotFoundException("x"));
        Assert.IsAssignableFrom<DomainException>(new DomainRuleViolationException("x"));
    }

    [Fact]
    public void NotFoundException_For_names_the_resource_and_the_identifier()
    {
        var exception = NotFoundException.For("Session", 42);

        Assert.Contains("Session", exception.Message, System.StringComparison.Ordinal);
        Assert.Contains("42", exception.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_domain_never_names_an_http_status_code()
    {
        // ADR-001 §4.4: mapping a failure to a status code is the host's job, in exactly
        // one place. This asserts the rule structurally rather than trusting review — if
        // someone adds a StatusCode property to a domain exception, this fails.
        var offenders = typeof(DomainException).Assembly
            .GetTypes()
            .Where(t => typeof(DomainException).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(p => p.Name.Contains("Status", System.StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains("HttpCode", System.StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.DeclaringType?.Name}.{p.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_domain_assembly_references_no_third_party_package()
    {
        // ADR-001 §4.2, the load-bearing decision: FitForge.Domain references nothing, so
        // a calculation cannot reach a DbContext. Training invariants 1 and 5 depend on
        // this staying true, and it is cheaper to fail here than to notice in review.
        var allowedPrefixes = new[] { "System", "netstandard", "mscorlib", "FitForge" };

        var unexpected = typeof(DomainException).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !allowedPrefixes.Any(p => name.StartsWith(p, System.StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(unexpected);
    }
}
