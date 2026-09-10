using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitForge.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Session"/> per <c>specs/002-identity-member-profile/data-model.md</c>,
/// including its three approved deviations from <c>database-rules.md</c>.
/// </summary>
public class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("Session");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).UseIdentityColumn();

        builder.Property(s => s.TokenHash)
            .HasColumnType("binary(32)")
            .IsRequired();

        // The only way in. Unique because two sessions cannot share a token, and indexed
        // because this is the lookup on every authenticated request.
        builder.HasIndex(s => s.TokenHash)
            .IsUnique()
            .HasDatabaseName("UQ_Session_TokenHash");

        // No navigation property, deliberately. Resolution joins Members explicitly so
        // that table's soft-delete filter decides whether the session is usable — a
        // filter on a required navigation is the kind of subtlety that produces a
        // different answer than the one the reader expects.
        builder.HasOne<Member>()
            .WithMany()
            .HasForeignKey(s => s.MemberId)
            .HasConstraintName("FK_Session_Member")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.MemberId).HasDatabaseName("IX_Session_MemberId");

        builder.Property(s => s.CreatedAtUtc).HasPrecision(3).IsRequired();
        builder.Property(s => s.ExpiresAtUtc).HasPrecision(3).IsRequired();
        builder.Property(s => s.RevokedAtUtc).HasPrecision(3);
        builder.Property(s => s.LastSeenAtUtc).HasPrecision(3).IsRequired();

        // No PublicId, no CreatedBy/UpdatedBy, no IsDeleted — data-model.md "Session",
        // approved in plan.md D3. Their absence is the decision; this comment is so the
        // next reader knows it was one.
    }
}
