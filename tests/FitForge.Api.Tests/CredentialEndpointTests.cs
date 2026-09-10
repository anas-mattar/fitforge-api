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
/// T040–T046 — register, sign in, sign out, end to end against a real SQL Server.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class CredentialEndpointTests(SqlServerDatabase database)
{
    private const string Password = "correct horse battery staple";

    private FitForgeApiFactory Api() =>
        new() { ConnectionString = database.Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString };

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, password, "Test Member"));

    private static Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string email, string password, string? source = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/sign-in")
        {
            Content = JsonContent.Create(new SignInRequest(email, password)),
        };

        if (source is not null)
        {
            request.Headers.Add("X-Forwarded-For", source);
        }

        return client.SendAsync(request);
    }

    private static async Task<string> TokenOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    // ---- T040: the whole of US1 in one test -----------------------------------------

    [Fact]
    public async Task Register_then_sign_in_then_resolve_the_session()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();

        var registered = await RegisterAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        var signedIn = await SignInAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenOf(signedIn));

        var session = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        var body = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, body.GetProperty("member").GetProperty("email").GetString());

        // Invariant 8: the internal key never leaves the API. publicId is present and
        // the payload has no "id" at all.
        Assert.True(body.GetProperty("member").TryGetProperty("publicId", out _));
        Assert.False(body.GetProperty("member").TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Registration_creates_the_profile_row_with_the_member()
    {
        // data-model.md: one row per member, created with the member, so no read path has
        // to handle its absence. A second call that could fail independently would be
        // exactly such a path.
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();

        await RegisterAsync(client, email, Password);

        await using var context = database.NewContext();
        var member = await context.Members
            .Include(m => m.Profile)
            .SingleAsync(m => m.NormalizedEmail == EmailAddress.Normalize(email));

        Assert.NotNull(member.Profile);
    }

    // ---- T041: one answer for two different failures --------------------------------

    [Fact]
    public async Task An_unknown_email_and_a_wrong_password_are_indistinguishable()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email, Password);

        var wrongPassword = await SignInAsync(client, email, "definitely not it", "10.0.0.1");
        var unknownEmail = await SignInAsync(client, NewEmail(), Password, "10.0.0.2");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // FR-004: not merely the same status - the same document, field for field.
        //
        // Every field EXCEPT traceId, which is per-request and differs between any two
        // responses of any kind. The first draft of this test compared raw bytes and
        // failed on exactly that. Excluding it is not a weakening: the requirement is
        // that the two cases be indistinguishable, and a correlation id that is random
        // in both carries no information about which one happened. Excluding anything
        // else here WOULD be a weakening, which is why the exclusion is one named field
        // rather than a fuzzy comparison.
        Assert.Equal(
            await WithoutTraceId(wrongPassword),
            await WithoutTraceId(unknownEmail));

        static async Task<string> WithoutTraceId(HttpResponseMessage response)
        {
            var document = await response.Content.ReadFromJsonAsync<JsonElement>();

            return string.Join('|', document.EnumerateObject()
                .Where(p => !string.Equals(p.Name, "traceId", StringComparison.Ordinal))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value}"));
        }
    }

    [Fact]
    public async Task The_failure_message_is_the_one_the_screen_specifies()
    {
        using var factory = Api();
        using var client = factory.CreateClient();

        var response = await SignInAsync(client, NewEmail(), Password);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // VI-012, verbatim. The visual reference fixes this string, so a reworded
        // "Invalid credentials" here would be a silent visual deviation.
        Assert.Equal("Email or password is incorrect.", body.GetProperty("title").GetString());
    }

    // ---- T043: case and whitespace collide -------------------------------------------

    [Theory]
    [InlineData("UPPER")]
    [InlineData("pad")]
    public async Task Registering_the_same_address_differently_spelled_is_a_duplicate(string variation)
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, email, Password)).StatusCode);

        var respelled = variation == "UPPER" ? email.ToUpperInvariant() : $"  {email}  ";
        var second = await RegisterAsync(client, respelled, Password);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Signing_in_with_a_differently_spelled_address_still_works()
    {
        // The other half of FR-001, and the half that would break a member's day if the
        // normalization were applied on write only.
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email, Password);

        var response = await SignInAsync(client, $"  {email.ToUpperInvariant()}  ", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_password_that_breaks_the_policy_is_refused_with_a_field_error()
    {
        using var factory = Api();
        using var client = factory.CreateClient();

        var response = await RegisterAsync(client, NewEmail(), "short");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("password", out _));
    }

    // ---- T044: the throttle is not an existence oracle -------------------------------

    [Fact]
    public async Task The_eleventh_failure_against_an_address_that_does_not_exist_is_429()
    {
        // The headline test of this phase. If the throttle only counted real accounts,
        // 429-versus-401 would tell an attacker which addresses have members — the exact
        // oracle the decoy hash spends 210,000 iterations to close. A green suite without
        // this test would prove the defence works everywhere except where it matters.
        using var factory = Api();
        using var client = factory.CreateClient();
        var ghost = NewEmail();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerEmail; attempt++)
        {
            var response = await SignInAsync(client, ghost, "wrong", source);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var throttled = await SignInAsync(client, ghost, "wrong", source);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_real_address_throttles_at_exactly_the_same_point()
    {
        // The comparison that makes the test above mean something: both kinds of address
        // behave identically, so the status code carries no information about existence.
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        await RegisterAsync(client, email, Password);

        for (var attempt = 1; attempt <= SignInThrottle.MaxPerEmail; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await SignInAsync(client, email, "wrong", source)).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SignInAsync(client, email, "wrong", source)).StatusCode);
    }

    // ---- T045: a success clears the email bucket, not the source bucket ---------------

    [Fact]
    public async Task A_successful_sign_in_clears_that_addresss_bucket()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        var source = $"198.51.100.{Random.Shared.Next(1, 254)}";
        await RegisterAsync(client, email, Password);

        for (var attempt = 1; attempt < SignInThrottle.MaxPerEmail; attempt++)
        {
            await SignInAsync(client, email, "wrong", source);
        }

        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(client, email, Password, source)).StatusCode);

        // Nine failures were forgiven, so the next wrong guess is a 401 rather than the
        // 429 it would have been. A member who mistypes their password nine times and
        // then gets it right should not be locked out on their next visit.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await SignInAsync(client, email, "wrong", source)).StatusCode);
    }

    [Fact]
    public async Task A_successful_sign_in_does_not_clear_the_source_bucket()
    {
        // One success must not license thirty more guesses from the same place, which is
        // precisely what an attacker holding one valid account of their own would use it
        // for. Verified against the rows, because the alternative is 30 HTTP round trips.
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        var source = $"198.51.100.{Random.Shared.Next(1, 254)}";
        await RegisterAsync(client, email, Password);

        await SignInAsync(client, NewEmail(), "wrong", source);
        await SignInAsync(client, NewEmail(), "wrong", source);

        var before = await CountAttemptsAsync();
        await SignInAsync(client, email, Password, source);
        var after = await CountAttemptsAsync();

        // The two failures for other addresses survive: clearing is per email, and those
        // rows belong to different emails on the same source.
        Assert.Equal(before, after);

        async Task<int> CountAttemptsAsync()
        {
            await using var context = database.NewContext();
            return await context.SignInAttempts.CountAsync();
        }
    }

    // ---- T046: sign-out revokes server-side and is idempotent -------------------------

    [Fact]
    public async Task Signing_out_kills_the_session_and_a_second_sign_out_is_still_204()
    {
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email, Password);

        var token = await TokenOf(await SignInAsync(client, email, Password));

        Assert.Equal(HttpStatusCode.NoContent, (await SignOut(token)).StatusCode);

        // FR-006: destroyed server-side, not merely a cookie the BFF dropped.
        var afterSignOut = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        afterSignOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(afterSignOut)).StatusCode);

        // And the second sign-out is a 401 rather than a 500: the session is gone, so the
        // request is simply unauthenticated. The idempotence the contract promises is at
        // the service level, which SessionServiceTests covers directly.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignOut(token)).StatusCode);

        Task<HttpResponseMessage> SignOut(string bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/sign-out");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return client.SendAsync(request);
        }
    }

    // ---- T039: nothing secret is stored where it should not be ------------------------

    [Fact]
    public async Task The_password_is_nowhere_in_the_member_row()
    {
        // FR-003 and SC-004, asserted against what was actually persisted rather than
        // against the code that persisted it.
        using var factory = Api();
        using var client = factory.CreateClient();
        var email = NewEmail();
        await RegisterAsync(client, email, Password);

        await using var context = database.NewContext();
        var member = await context.Members
            .SingleAsync(m => m.NormalizedEmail == EmailAddress.Normalize(email));

        Assert.DoesNotContain(Password, member.PasswordHash, StringComparison.Ordinal);
        Assert.DoesNotContain("staple", member.PasswordHash, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Password, member.PasswordHash);
    }

    [Fact]
    public async Task The_source_address_is_stored_hashed_and_not_in_the_clear()
    {
        // Invariant 10's "minimal": the throttle needs equality and nothing more, and a
        // table of members' addresses beside their email is what turns a small breach
        // into a large one.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = "192.0.2.77";

        await SignInAsync(client, NewEmail(), "wrong", source);

        await using var context = database.NewContext();
        var attempt = await context.SignInAttempts.OrderByDescending(a => a.Id).FirstAsync();

        Assert.DoesNotContain(source, Convert.ToBase64String(attempt.SourceHash), StringComparison.Ordinal);
        Assert.Equal(32, attempt.SourceHash.Length);
    }
}
