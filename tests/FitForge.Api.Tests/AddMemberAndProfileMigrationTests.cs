using System;
using System.Linq;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FitForge.Api.Tests;

/// <summary>
/// T011 — the <c>AddMemberAndProfile</c> migration creates exactly what
/// <c>specs/002-identity-member-profile/data-model.md</c> says, and its down-path drops
/// exactly that and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read against operations, not against a database.</b> <c>tasks.md</c> A1, approved by
/// anas.m on 2026-09-10: no SQL Server instance is reachable from the authoring host, and
/// CI has none either — 001's tests stub the database rather than provisioning one.
/// Applying a migration needs a real server; reading the operations it would issue does
/// not, and it settles the question T011 exists to settle.
/// </para>
/// <para>
/// <b>What this cannot catch</b>, stated so nobody mistakes a green run for more than it
/// is: a migration that is valid C# but fails against SQL Server — a check-constraint
/// expression the parser rejects, say. That risk lands on the first real
/// <c>database update</c>, and <c>rollback.md</c>'s verification list is where it is
/// carried.
/// </para>
/// </remarks>
public class AddMemberAndProfileMigrationTests
{
    private static Migration TheMigration()
    {
        var options = new DbContextOptionsBuilder<FitForgeDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True")
            .Options;

        // No connection is opened: the migrations assembly is read from the model, which
        // is built from code. UseSqlServer supplies the provider the operations are
        // generated for, and nothing more.
        using var context = new FitForgeDbContext(options);

        var assembly = context.GetService<IMigrationsAssembly>();

        var found = assembly.Migrations
            .Where(m => m.Key.EndsWith("AddMemberAndProfile", StringComparison.Ordinal))
            .ToArray();

        // Exactly one, by name. Two migrations whose ids end the same way would make
        // every assertion below ambiguous about which one it graded.
        var single = Assert.Single(found);

        return assembly.CreateMigration(single.Value, "Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void Up_creates_exactly_Member_and_Profile()
    {
        var created = TheMigration().UpOperations
            .OfType<CreateTableOperation>()
            .Select(o => o.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Member", "Profile" }, created);
    }

    [Fact]
    public void Up_drops_nothing_and_alters_nothing()
    {
        // The migration is purely additive, which is what makes rollback.md's
        // "Database Rollback" section short. If a later edit adds a Drop or an Alter to
        // this migration, that claim stops being true and this test says so.
        var operations = TheMigration().UpOperations;

        Assert.Empty(operations.OfType<DropTableOperation>());
        Assert.Empty(operations.OfType<DropColumnOperation>());
        Assert.Empty(operations.OfType<AlterColumnOperation>());
        Assert.Empty(operations.OfType<AlterTableOperation>());
    }

    [Fact]
    public void Down_drops_exactly_the_two_tables_it_created()
    {
        var dropped = TheMigration().DownOperations
            .OfType<DropTableOperation>()
            .Select(o => o.Name)
            .ToArray();

        // Order matters, and it is not cosmetic: FK_Profile_Member is RESTRICT, so
        // dropping Member first would fail against a real server. Asserting the order
        // here is the closest this test gets to executing it.
        Assert.Equal(new[] { "Profile", "Member" }, dropped);
    }

    [Fact]
    public void Down_does_nothing_except_drop_those_tables()
    {
        // rollback.md: "both down methods drop only the tables their up created". A down
        // that also deleted rows, or touched a table this migration did not create, would
        // be a rollback with a blast radius — the thing that document exists to bound.
        var operations = TheMigration().DownOperations;

        Assert.All(operations, o => Assert.IsType<DropTableOperation>(o));
    }

    [Fact]
    public void Up_creates_the_uniqueness_the_login_identity_depends_on()
    {
        // FR-001 is enforced in two places on purpose: EmailAddress.Normalize in code and
        // this index in the schema. Application-only enforcement is one forgotten code
        // path away from two accounts on one address.
        var unique = TheMigration().UpOperations
            .OfType<CreateIndexOperation>()
            .Where(o => o.IsUnique)
            .Select(o => o.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "UQ_Member_NormalizedEmail", "UQ_Member_PublicId", "UQ_Profile_MemberId", "UQ_Profile_PublicId" },
            unique);
    }

    [Fact]
    public void The_foreign_key_to_Member_restricts_rather_than_cascades()
    {
        // Invariant 10's physical removal is a deliberate act by the retention service
        // (phase 6). A cascade here would make deleting a member a side effect of a
        // foreign key instead — database-rules.md forbids exactly that.
        var foreignKey = TheMigration().UpOperations
            .OfType<CreateTableOperation>()
            .Single(t => t.Name == "Profile")
            .ForeignKeys
            .Single();

        Assert.Equal("FK_Profile_Member", foreignKey.Name);
        Assert.Equal("Member", foreignKey.PrincipalTable);
        Assert.Equal(ReferentialAction.Restrict, foreignKey.OnDelete);
    }
}
