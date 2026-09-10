using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Infrastructure.Persistence;

/// <summary>
/// FitForge's only database context.
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
    /// <summary>
    /// Members. A global query filter hides soft-deleted rows, so a caller has to opt in
    /// to seeing them rather than remember to exclude them.
    /// </summary>
    public DbSet<Member> Members => Set<Member>();

    /// <summary>
    /// Member profiles, one per member, same filter.
    /// </summary>
    public DbSet<Profile> Profiles => Set<Profile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(InfrastructureAssembly.Reference);
    }
}
