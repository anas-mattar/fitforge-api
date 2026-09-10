using System;
using System.Threading.Tasks;
using FitForge.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Tests;

/// <summary>
/// A real SQL Server database, created for the test run and dropped after it.
/// </summary>
/// <remarks>
/// <para>
/// <c>tasks.md</c> A4, approved by anas.m on 2026-09-10, option (a). The alternatives —
/// SQLite or the in-memory provider — were rejected because this feature's headline claim
/// is that one member cannot read another's data, and that claim is carried by a unique
/// index, a foreign key and a global query filter. Testing it against a different engine
/// tests a different thing; testing it against the in-memory provider, which enforces no
/// constraint at all, would produce a green run that means nothing.
/// </para>
/// <para>
/// <b>Migrations, not <c>EnsureCreated</c>.</b> <c>EnsureCreated</c> builds the schema from
/// the model, so it would test a schema that never ships and would silently diverge the
/// day a migration and the model disagree — which is exactly the failure a migration test
/// exists to catch. Applying the migrations also retires the residual A1 left behind:
/// T011 could not catch a migration that is valid C# but invalid SQL, and from here on
/// every test run does.
/// </para>
/// <para>
/// <b>Where the connection string comes from.</b> <c>FITFORGE_TEST_SQL</c> if set — that is
/// what CI passes — otherwise LocalDB, so a developer on Windows needs no setup and no
/// credential. Nothing here is a secret and nothing here is in source: the fallback uses
/// a trusted connection, and CI's value carries a password from a repository secret.
/// </para>
/// </remarks>
public sealed class SqlServerDatabase : IAsyncLifetime
{
    private const string ConnectionStringVariable = "FITFORGE_TEST_SQL";

    /// <summary>
    /// LocalDB, trusted connection, no credential. Windows-only, which is why CI sets the
    /// variable instead of relying on this.
    /// </summary>
    private const string LocalDbFallback =
        @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string _databaseName = $"FitForgeTest_{Guid.NewGuid():N}";

    private string _connectionString = string.Empty;

    /// <summary>
    /// Options for a context pointing at this run's database. Each call returns fresh
    /// options so a test can hold two contexts and see what the other actually wrote,
    /// rather than what a shared change tracker remembers.
    /// </summary>
    public DbContextOptions<FitForgeDbContext> Options =>
        new DbContextOptionsBuilder<FitForgeDbContext>()
            .UseSqlServer(_connectionString)
            .Options;

    public FitForgeDbContext NewContext() => new(Options);

    public async Task InitializeAsync()
    {
        var baseConnectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable) ?? LocalDbFallback;

        // The name is ours, not the caller's: a run must never point at a database
        // somebody else is using. database-rules.md "Setup" is about exactly this
        // mistake, and a fixture that honoured an Initial Catalog from the environment
        // would be a scripted version of it.
        _connectionString = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = _databaseName,
        }.ConnectionString;

        await using var context = NewContext();

        // Creates the database and applies every migration in order.
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var context = NewContext();

        // Physical deletion of a throwaway test database, which is not what
        // rollback-process.md's prohibition is about: that protects domain records in an
        // environment holding real member data. This holds rows this run invented
        // seconds ago.
        await context.Database.EnsureDeletedAsync();
    }
}

/// <summary>
/// Shares one database across the tests that need one.
/// </summary>
/// <remarks>
/// One creation and one migration run per test class rather than per test. Tests inside a
/// class must therefore not assume an empty database — each seeds the member it needs and
/// asserts on that member's rows, which is closer to how the code behaves in production
/// anyway.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerDatabase>
{
    public const string Name = "sql-server";
}
