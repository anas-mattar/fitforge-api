using System;
using System.Reflection;
using FitForge.Infrastructure;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace FitForge.Api.Tests;

/// <summary>
/// The startup message for a missing connection string recommends a command. This proves
/// the command runs.
/// </summary>
/// <remarks>
/// Before phase 7 it did not: `FitForge.Api.csproj` had no `UserSecretsId`, so
/// `dotnet user-secrets set` exited with "Could not find the global property
/// 'UserSecretsId'". A startup message exists to save its reader a search, and one that
/// sends them to a command that fails costs them the search anyway — while telling them
/// they are following instructions.
/// </remarks>
public class UserSecretsIdTests
{
    [Fact]
    public void The_api_assembly_declares_a_user_secrets_id()
    {
        // The MSBuild property is emitted as this assembly attribute, so its presence
        // here is the same fact the dotnet CLI reads. Regenerating the csproj without the
        // property fails this test rather than silently restoring the defect.
        var attribute = typeof(Program).Assembly.GetCustomAttribute<UserSecretsIdAttribute>();

        Assert.NotNull(attribute);
        Assert.False(string.IsNullOrWhiteSpace(attribute.UserSecretsId));
    }

    [Fact]
    public void The_startup_message_recommends_only_what_works()
    {
        var message = DatabaseOptions.MissingConnectionStringMessage;

        // Both routes are named, because which one works depends on the environment.
        Assert.Contains("Database__ConnectionString", message, StringComparison.Ordinal);
        Assert.Contains("dotnet user-secrets set", message, StringComparison.Ordinal);

        // And the constraint is stated rather than left to be discovered: user secrets are
        // loaded only in Development, so the same advice fails everywhere else.
        Assert.Contains("Development", message, StringComparison.Ordinal);
    }
}
