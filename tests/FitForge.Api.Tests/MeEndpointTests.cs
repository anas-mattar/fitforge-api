using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FitForge.Api.Features.Identity;
using FitForge.Domain.Members;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Tests;

/// <summary>
/// T055–T060 — the `/me` surface, against a real SQL Server.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class MeEndpointTests(SqlServerDatabase database)
{
    private const string Password = "correct horse battery staple";

    private FitForgeApiFactory Api() =>
        new() { ConnectionString = database.Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString };

    private static async Task<(string Token, string Email)> NewMemberAsync(HttpClient client)
    {
        var email = $"member-{Guid.NewGuid():N}@example.com";
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, Password, "Test Member"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("token").GetString()!, email);
    }

    private static HttpRequestMessage As(string token, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    // ---- T055 / D13-4: units are a rendering instruction ----------------------------

    [Fact]
    public async Task Changing_units_leaves_every_measurement_column_byte_identical()
    {
        // FR-011, invariant 4, VI-028. The property a future "helpful" refactor would
        // break: converting stored values on preference change looks like a kindness and
        // is how 100.0 kg becomes 99.8 after three edits.
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        // Give the member a height to convert, so the test has something to get wrong.
        await using (var seed = database.NewContext())
        {
            var profile = await seed.Profiles
                .SingleAsync(p => p.Member!.NormalizedEmail == EmailAddress.Normalize(email));
            profile.HeightCm = 167.50m;
            await seed.SaveChangesAsync();
        }

        var before = await HeightAsync();

        var response = await client.SendAsync(
            As(token, HttpMethod.Patch, "/api/v1/me/preferences", new { units = "Imperial" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = await HeightAsync();

        Assert.Equal(before, after);
        Assert.Equal(167.50m, after);

        // And the API still reports centimetres — conversion is the browser's job.
        var read = await client.SendAsync(As(token, HttpMethod.Get, "/api/v1/me"));
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(167.50m, body.GetProperty("profile").GetProperty("heightCm").GetDecimal());

        async Task<decimal?> HeightAsync()
        {
            await using var context = database.NewContext();
            return await context.Profiles
                .Where(p => p.Member!.NormalizedEmail == EmailAddress.Normalize(email))
                .Select(p => p.HeightCm)
                .SingleAsync();
        }
    }

    [Fact]
    public async Task Preferences_persist_and_an_absent_field_is_left_alone()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, _) = await NewMemberAsync(client);

        await client.SendAsync(As(token, HttpMethod.Patch, "/api/v1/me/preferences",
            new { goal = "Strength", experience = "Advanced" }));

        // Only units this time. goal and experience are absent, and absent means
        // unchanged — not cleared, which is the bug a nullable-record binding produces.
        await client.SendAsync(As(token, HttpMethod.Patch, "/api/v1/me/preferences",
            new { units = "Imperial" }));

        var body = await (await client.SendAsync(As(token, HttpMethod.Get, "/api/v1/me")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var member = body.GetProperty("member");

        Assert.Equal("Imperial", member.GetProperty("units").GetString());
        Assert.Equal("Strength", member.GetProperty("goal").GetString());
        Assert.Equal("Advanced", member.GetProperty("experience").GetString());
    }

    [Fact]
    public async Task An_explicit_null_is_refused_rather_than_treated_as_absent()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, _) = await NewMemberAsync(client);

        var request = As(token, HttpMethod.Patch, "/api/v1/me/preferences");
        request.Content = new StringContent(
            """{"timeZone": null}""", System.Text.Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---- T059: an unresolvable zone is refused, never silently UTC -------------------

    [Fact]
    public async Task A_time_zone_the_host_cannot_resolve_is_a_422()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, _) = await NewMemberAsync(client);

        var response = await client.SendAsync(As(token, HttpMethod.Patch, "/api/v1/me/preferences",
            new { timeZone = "Mars/Olympus_Mons" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // And the stored value is untouched — a rejected change must not half-apply.
        var body = await (await client.SendAsync(As(token, HttpMethod.Get, "/api/v1/me")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UTC", body.GetProperty("member").GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task A_real_iana_zone_is_accepted()
    {
        // The companion. Without it, a Resolves() that always returned false would pass
        // the test above.
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, _) = await NewMemberAsync(client);

        var response = await client.SendAsync(As(token, HttpMethod.Patch, "/api/v1/me/preferences",
            new { timeZone = "Asia/Kuala_Lumpur" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Asia/Kuala_Lumpur", body.GetProperty("timeZone").GetString());
    }

    // ---- T056 / T057: password change -------------------------------------------------

    [Fact]
    public async Task Changing_the_password_kills_other_sessions_and_keeps_this_one()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (first, email) = await NewMemberAsync(client);

        // A second session, as if from another device.
        var second = (await (await client.PostAsJsonAsync("/api/v1/auth/sign-in",
                new SignInRequest(email, Password)))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        const string Replacement = "a completely different passphrase";

        var change = await client.SendAsync(As(first, HttpMethod.Post, "/api/v1/me/password",
            new { currentPassword = Password, newPassword = Replacement }));

        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // FR-013: every OTHER session is dead...
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(As(second, HttpMethod.Get, "/api/v1/me"))).StatusCode);

        // ...and the tab in front of the member survives.
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(As(first, HttpMethod.Get, "/api/v1/me"))).StatusCode);

        // The old password stops working and the new one starts.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, Replacement))).StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_refuses_the_change_and_leaves_the_old_one_working()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        var response = await client.SendAsync(As(token, HttpMethod.Post, "/api/v1/me/password",
            new { currentPassword = "not it", newPassword = "a completely different passphrase" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // Distinguishable from sign-in's message on purpose: the caller already proved
        // they are this member, so naming the wrong field leaks nothing.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Your current password is incorrect.", body.GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/v1/auth/sign-in", new SignInRequest(email, Password))).StatusCode);
    }

    [Fact]
    public async Task A_new_password_that_breaks_the_policy_is_refused()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, _) = await NewMemberAsync(client);

        var response = await client.SendAsync(As(token, HttpMethod.Post, "/api/v1/me/password",
            new { currentPassword = Password, newPassword = "short" }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---- T060: two changes at once -----------------------------------------------------

    [Fact]
    public async Task Two_concurrent_password_changes_leave_exactly_one_winner()
    {
        // Both requests verify the ORIGINAL password, so a plain update would let the
        // second silently overwrite the first — leaving the member holding a password
        // nobody knowingly chose. The compare-and-swap on the observed hash means one
        // wins and the other is told.
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        const string First = "the first replacement phrase";
        const string Second = "the second replacement phrase";

        var results = await Task.WhenAll(
            client.SendAsync(As(token, HttpMethod.Post, "/api/v1/me/password",
                new { currentPassword = Password, newPassword = First })),
            client.SendAsync(As(token, HttpMethod.Post, "/api/v1/me/password",
                new { currentPassword = Password, newPassword = Second })));

        var succeeded = results.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Equal(1, succeeded);

        // The safety property, which matters more than the count: the account is never
        // left with NEITHER password working.
        var works = 0;
        foreach (var candidate in new[] { First, Second })
        {
            var signIn = await client.PostAsJsonAsync("/api/v1/auth/sign-in",
                new SignInRequest(email, candidate));

            if (signIn.StatusCode == HttpStatusCode.OK)
            {
                works++;
            }
        }

        Assert.Equal(1, works);
    }

    // ---- T058: deletion -----------------------------------------------------------------

    [Fact]
    public async Task Deleting_signs_the_member_out_and_makes_sign_in_indistinguishable_from_a_wrong_password()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        var deleted = await client.SendAsync(
            As(token, HttpMethod.Delete, "/api/v1/me", new { password = Password }));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Signed out immediately — every session, the presented one included.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(As(token, HttpMethod.Get, "/api/v1/me"))).StatusCode);

        // FR-014: a deleted account is not discoverable. Same 401 as any wrong password.
        var afterDelete = await client.PostAsJsonAsync("/api/v1/auth/sign-in",
            new SignInRequest(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);

        var body = await afterDelete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Email or password is incorrect.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Deletion_soft_deletes_the_member_and_the_profile_and_removes_nothing()
    {
        // Invariant 10's first half. The second half — physical removal after the
        // retention window — is phase 6's, and nothing here may pre-empt it.
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        await client.SendAsync(As(token, HttpMethod.Delete, "/api/v1/me", new { password = Password }));

        await using var context = database.NewContext();
        var normalized = EmailAddress.Normalize(email);

        var member = await context.Members
            .IgnoreQueryFilters()
            .SingleAsync(m => m.NormalizedEmail == normalized);

        Assert.True(member.IsDeleted);
        Assert.NotNull(member.DeletedAtUtc);
        Assert.Equal(member.PublicId.ToString(), member.DeletedBy);

        var profile = await context.Profiles
            .IgnoreQueryFilters()
            .SingleAsync(p => p.MemberId == member.Id);

        Assert.True(profile.IsDeleted);
    }

    [Fact]
    public async Task Deleting_with_a_wrong_password_deletes_nothing()
    {
        // Deletion re-authenticates: a stolen session must not be able to destroy an
        // account. Of everything in this feature, this is the action with no undo once
        // the retention window passes.
        using var factory = Api();
        using var client = factory.CreateClient();
        var (token, email) = await NewMemberAsync(client);

        var response = await client.SendAsync(
            As(token, HttpMethod.Delete, "/api/v1/me", new { password = "not it" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(As(token, HttpMethod.Get, "/api/v1/me"))).StatusCode);

        await using var context = database.NewContext();
        var member = await context.Members
            .IgnoreQueryFilters()
            .SingleAsync(m => m.NormalizedEmail == EmailAddress.Normalize(email));

        Assert.False(member.IsDeleted);
    }
}
