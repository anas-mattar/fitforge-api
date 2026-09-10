using Microsoft.EntityFrameworkCore;

namespace FitForge.Infrastructure.Persistence;

/// <summary>
/// FitForge's only database context. Carries no <c>DbSet</c> yet — the first entities
/// arrive with feature 002.
/// </summary>
/// <remarks>
/// <para>
/// ADR-001 §4.3: this type, every entity configuration and every migration live in this
/// project and nowhere else. Feature handlers in FitForge.Api may query through it; they
/// may not configure it.
/// </para>
/// <para>
/// Configurations are discovered by scanning this assembly, so adding an entity means
/// adding one <c>IEntityTypeConfiguration&lt;T&gt;</c> file and nothing else — no
/// registration list to remember, and therefore no registration list to forget.
/// </para>
/// </remarks>
public class FitForgeDbContext(DbContextOptions<FitForgeDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(InfrastructureAssembly.Reference);
    }
}
