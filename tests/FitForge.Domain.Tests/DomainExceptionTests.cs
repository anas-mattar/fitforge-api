using System.Linq;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
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
    public void The_domain_project_declares_no_package_reference()
    {
        // ADR-001 §4.2, the load-bearing decision: FitForge.Domain references nothing, so
        // a calculation cannot reach a DbContext. Training invariants 1 and 5 depend on
        // it staying true.
        //
        // Read from the project file, not from the assembly. Until phase 8 this test
        // called GetReferencedAssemblies(), which lists the assemblies the compiler
        // actually emitted references to — so a PackageReference nobody had used yet
        // passed it, and the guard only bit once someone wrote the line of code it was
        // supposed to prevent them from being able to write. The csproj comment, ADR-001
        // §4.3 and the phase 1 commit message all claimed more than that test delivered.
        var project = XDocument.Load(DomainProjectPath());

        var packages = project.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.Empty(packages);
    }

    [Fact]
    public void The_domain_project_declares_no_project_reference_either()
    {
        // The dependency direction is Api -> Infrastructure -> Domain -> nothing. A
        // ProjectReference here would invert it just as effectively as a package.
        var project = XDocument.Load(DomainProjectPath());

        var references = project.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.Empty(references);
    }

    /// <summary>
    /// Locates <c>FitForge.Domain.csproj</c> by walking up from the test binaries to the
    /// repository root, so the test does not encode a build-output-relative path that
    /// breaks the first time the output layout changes.
    /// </summary>
    private static string DomainProjectPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FitForge.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var path = Path.Combine(directory.FullName, "src", "FitForge.Domain", "FitForge.Domain.csproj");
        Assert.True(File.Exists(path), $"Expected the domain project at {path}");

        return path;
    }
}
