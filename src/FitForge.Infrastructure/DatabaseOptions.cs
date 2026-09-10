namespace FitForge.Infrastructure;

/// <summary>
/// Database configuration, validated at startup.
/// </summary>
/// <remarks>
/// The connection string is a value, never source. <c>appsettings.json</c> carries the
/// setting's <em>name</em> with an empty value so the shape is discoverable; the value
/// comes from the environment (constitution VI, and FR-010).
/// </remarks>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section this binds to.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// What a developer sees when they run the API without configuring a database.
    /// </summary>
    /// <remarks>
    /// It names the setting in both of the forms they might need to type, because
    /// "connection string not configured" sends people to search the codebase, and the
    /// point of failing at startup is to not cost them that search.
    /// </remarks>
    public const string MissingConnectionStringMessage =
        "No database connection string. Set 'Database:ConnectionString' — as the environment " +
        "variable Database__ConnectionString, or for local development with " +
        "'dotnet user-secrets set \"Database:ConnectionString\" \"...\"'. It is deliberately empty " +
        "in appsettings.json: the name belongs in source, the value does not.";

    /// <summary>SQL Server connection string. Supplied by the environment.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}
