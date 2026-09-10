using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitForge.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="SignInAttempt"/> — the throttle's counter.
/// </summary>
public class SignInAttemptConfiguration : IEntityTypeConfiguration<SignInAttempt>
{
    public void Configure(EntityTypeBuilder<SignInAttempt> builder)
    {
        builder.ToTable("SignInAttempt");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityColumn();

        builder.Property(a => a.NormalizedEmail).IsRequired().HasMaxLength(254);
        builder.Property(a => a.SourceHash).HasColumnType("binary(32)").IsRequired();
        builder.Property(a => a.AttemptedAtUtc).HasPrecision(3).IsRequired();

        // Both counting queries filter on (email, time) and (source, time). Composite
        // indexes in that order, because the window predicate is always a range and a
        // range column belongs last.
        builder.HasIndex(a => new { a.NormalizedEmail, a.AttemptedAtUtc })
            .HasDatabaseName("IX_SignInAttempt_Email_At");

        builder.HasIndex(a => new { a.SourceHash, a.AttemptedAtUtc })
            .HasDatabaseName("IX_SignInAttempt_Source_At");

        // No foreign key to Member, and that is not an oversight: an attempt is recorded
        // for addresses that do not exist. A foreign key would make the table unable to
        // hold exactly the rows it exists to hold.
    }
}
