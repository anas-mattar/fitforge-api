using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitForge.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Member"/> to the schema
/// <c>specs/002-identity-member-profile/data-model.md</c> specifies.
/// </summary>
/// <remarks>
/// <c>database-rules.md</c>, "Constraints Mirror Invariants": every domain rule a
/// constraint can express is one here as well as in application code. The check
/// constraints below are not belt-and-braces — they are the half that survives a code path
/// nobody remembered to write.
/// </remarks>
public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("Member");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).UseIdentityColumn();

        builder.Property(m => m.PublicId).IsRequired();
        builder.HasIndex(m => m.PublicId)
            .IsUnique()
            .HasDatabaseName("UQ_Member_PublicId");

        builder.Property(m => m.Email).IsRequired().HasMaxLength(254);

        builder.Property(m => m.NormalizedEmail).IsRequired().HasMaxLength(254);
        builder.HasIndex(m => m.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("UQ_Member_NormalizedEmail");

        builder.Property(m => m.PasswordHash).IsRequired().HasMaxLength(256);

        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(60);

        builder.Property(m => m.Units).HasConversion<byte>().IsRequired();
        builder.Property(m => m.Goal).HasConversion<byte>().IsRequired();
        builder.Property(m => m.Experience).HasConversion<byte>().IsRequired();

        builder.Property(m => m.TimeZone).IsRequired().HasMaxLength(64).HasDefaultValue("UTC");

        builder.Property(m => m.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(m => m.CreatedBy).IsRequired().HasMaxLength(64);
        builder.Property(m => m.UpdatedAtUtc).HasPrecision(3);
        builder.Property(m => m.UpdatedBy).HasMaxLength(64);
        builder.Property(m => m.IsDeleted).HasDefaultValue(false).IsRequired();
        builder.Property(m => m.DeletedAtUtc).HasPrecision(3);
        builder.Property(m => m.DeletedBy).HasMaxLength(64);

        // Enums are stored as TINYINT, so an out-of-range byte is a value the C# type
        // system cannot express but the database can. Bounding each column here means a
        // hand-written UPDATE cannot leave a row that no enum member describes.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Member_Units", "[Units] IN (0, 1)");
            t.HasCheckConstraint("CK_Member_Goal", "[Goal] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_Member_Experience", "[Experience] BETWEEN 0 AND 2");
        });

        // Soft-deleted members are invisible unless a query opts in with
        // IgnoreQueryFilters(). The retention service (phase 6) is the one caller that
        // does, and it is the only place a member is physically removed (invariant 10).
        builder.HasQueryFilter(m => !m.IsDeleted);
    }
}
