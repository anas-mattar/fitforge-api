using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitForge.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Profile"/> per <c>specs/002-identity-member-profile/data-model.md</c>.
/// </summary>
public class ProfileConfiguration : IEntityTypeConfiguration<Profile>
{
    public void Configure(EntityTypeBuilder<Profile> builder)
    {
        builder.ToTable("Profile");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).UseIdentityColumn();

        builder.Property(p => p.PublicId).IsRequired();
        builder.HasIndex(p => p.PublicId)
            .IsUnique()
            .HasDatabaseName("UQ_Profile_PublicId");

        // One profile per member, and the member cannot be removed out from under it.
        // Restrict, never cascade: database-rules.md forbids cascade delete to data that
        // history references, and invariant 10's physical removal is a deliberate act by
        // the retention service, not a side effect of a foreign key.
        builder.HasOne(p => p.Member)
            .WithOne(m => m.Profile)
            .HasForeignKey<Profile>(p => p.MemberId)
            .HasConstraintName("FK_Profile_Member")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.MemberId)
            .IsUnique()
            .HasDatabaseName("UQ_Profile_MemberId");

        builder.Property(p => p.BirthYear);
        builder.Property(p => p.Sex).HasConversion<byte?>();
        builder.Property(p => p.HeightCm).HasPrecision(5, 2);

        builder.Property(p => p.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(p => p.CreatedBy).IsRequired().HasMaxLength(64);
        builder.Property(p => p.UpdatedAtUtc).HasPrecision(3);
        builder.Property(p => p.UpdatedBy).HasMaxLength(64);
        builder.Property(p => p.IsDeleted).HasDefaultValue(false).IsRequired();
        builder.Property(p => p.DeletedAtUtc).HasPrecision(3);
        builder.Property(p => p.DeletedBy).HasMaxLength(64);

        // The upper bound is a literal rather than a computed current year: a check
        // constraint is baked into the schema at migration time, so YEAR(GETDATE()) would
        // freeze to whatever the migration ran in and then quietly drift. 2200 is
        // deliberately loose — the constraint is here to reject 19 and 20260, not to
        // adjudicate plausible ages, which is application validation's job.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Profile_BirthYear", "[BirthYear] IS NULL OR [BirthYear] BETWEEN 1900 AND 2200");
            t.HasCheckConstraint("CK_Profile_Sex", "[Sex] IS NULL OR [Sex] BETWEEN 0 AND 2");
            t.HasCheckConstraint("CK_Profile_HeightCm", "[HeightCm] IS NULL OR [HeightCm] > 0");
        });

        builder.HasQueryFilter(p => !p.IsDeleted);
    }
}
