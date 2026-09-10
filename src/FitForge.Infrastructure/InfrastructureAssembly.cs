namespace FitForge.Infrastructure;

/// <summary>
/// Anchor for assembly scanning — the stable type <c>typeof(...)</c> can point at when
/// something needs "the persistence assembly" without naming a class that might move.
/// </summary>
/// <remarks>
/// Phase 2 uses it for <c>ApplyConfigurationsFromAssembly</c>, so that adding an entity
/// configuration means adding one file and nothing else.
/// </remarks>
public static class InfrastructureAssembly
{
    public static System.Reflection.Assembly Reference => typeof(InfrastructureAssembly).Assembly;
}
