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
/// T115, T120, T121 — which requests the throttle can see, and whose word it takes for who
/// they came from.
/// </summary>
/// <remarks>
/// Feature 002's review found the throttle guarding one of the four paths that verify a
/// password, and keying its per-source bucket on a header nothing sent (findings F1, F3,
/// F6). Every test here is about reach rather than arithmetic —
/// <see cref="SignInThrottleTests"/> covers the counting.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class ThrottleReachTests(SqlServerDatabase database)
{
    private const string Password = "correct horse battery staple";

    private string ConnectionString => database.Options.Extensions
        .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
        .Single().ConnectionString!;

    private FitForgeApiFactory Api(string? peer = null) =>
        new()
        {
            ConnectionString = ConnectionString,
            PeerAddress = peer ?? FitForgeApiFactory.TrustedPeer,
        };

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string? forwardedFor = null,
        string? token = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };

        if (forwardedFor is not null)
        {
            request.Headers.Add(SourceAddress.ForwardedForHeader, forwardedFor);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string email, string password, string? forwardedFor = null) =>
        SendAsync(client, HttpMethod.Post, "/api/v1/auth/sign-in",
            new SignInRequest(email, password), forwardedFor);

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string email, string? forwardedFor = null) =>
        SendAsync(client, HttpMethod.Post, "/api/v1/auth/register",
            new RegisterRequest(email, Password, "Test Member"), forwardedFor);

    private async Task<byte[]> LatestSourceHashAsync()
    {
        await using var context = database.NewContext();
        return await context.SignInAttempts
            .OrderByDescending(a => a.Id)
            .Select(a => a.SourceHash)
            .FirstAsync();
    }

    // ---- T115: whose claim about the source address is believed ----------------------

    [Fact]
    public async Task A_caller_that_is_not_a_configured_proxy_cannot_choose_its_own_bucket()
    {
        // Finding F1, second half. The endpoint used to read X-Forwarded-For from anyone,
        // so a caller reaching the API directly got a fresh bucket per request by varying
        // one string — a throttle that counts and never refuses.
        using var factory = Api(peer: "198.51.100.9");
        using var client = factory.CreateClient();

        await SignInAsync(client, NewEmail(), "wrong", forwardedFor: "203.0.113.1");
        var first = await LatestSourceHashAsync();

        await SignInAsync(client, NewEmail(), "wrong", forwardedFor: "203.0.113.2");
        var second = await LatestSourceHashAsync();

        // Same bucket both times: the header was ignored and the connection's own address
        // was used, which is the one thing the caller cannot forge.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_configured_proxy_is_believed()
    {
        // The other half of the same rule, and the reason the test above is not simply
        // "the header is always ignored".
        using var factory = Api();
        using var client = factory.CreateClient();

        await SignInAsync(client, NewEmail(), "wrong", forwardedFor: "203.0.113.1");
        var first = await LatestSourceHashAsync();

        await SignInAsync(client, NewEmail(), "wrong", forwardedFor: "203.0.113.2");
        var second = await LatestSourceHashAsync();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_proxy_that_names_nobody_leaves_the_source_unknown()
    {
        // Not "the proxy's own address". Every member behind the BFF would then share one
        // bucket, which is the outage F1 describes — and it is what shipped, because the
        // BFF never sent the header at all.
        using var factory = Api();
        using var client = factory.CreateClient();

        await SignInAsync(client, NewEmail(), "wrong");
        var unknown = await LatestSourceHashAsync();

        await SignInAsync(client, NewEmail(), "wrong", forwardedFor: FitForgeApiFactory.TrustedPeer);
        var named = await LatestSourceHashAsync();

        // The proxy naming itself is treated the same way as naming nobody: the chain is
        // longer than the deployment expects and the caller is genuinely not known.
        Assert.Equal(unknown, named);
    }

    // ---- T120: register is throttled, and its 409 costs no hash ----------------------

    [Fact]
    public async Task Probing_one_address_on_register_runs_out_of_attempts()
    {
        // Finding F3. contracts/auth.md §2 has listed a 429 under register since before
        // implementation and nothing produced one, so 409-versus-201 was an unlimited
        // unauthenticated existence oracle — the one §3 spends a decoy hash to close.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var email = NewEmail();

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, email, source)).StatusCode);

        // Each probe is a 409: the address now exists. Registration cleared the bucket on
        // success, so the count starts from this point.
        for (var probe = 1; probe <= SignInThrottle.MaxPerEmail; probe++)
        {
            Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(client, email, source)).StatusCode);
        }

        var throttled = await RegisterAsync(client, email, source);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    [Fact]
    public async Task Registering_successfully_costs_nothing_in_either_bucket()
    {
        // §6 counts FAILED attempts. A member who registers has not failed at anything, and
        // leaving the row would make thirty registrations from one office throttle the
        // thirty-first.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var email = NewEmail();

        await RegisterAsync(client, email, source);

        await using var context = database.NewContext();
        Assert.Equal(0, await context.SignInAttempts
            .CountAsync(a => a.NormalizedEmail == EmailAddress.Normalize(email)));
    }

    // ---- T121: the /me re-authentications are throttled ------------------------------

    [Fact]
    public async Task Guessing_the_current_password_runs_out_of_attempts()
    {
        // Finding F6. A stolen session could guess `currentPassword` at request rate
        // forever, reading 401-versus-422-versus-204 as a clean oracle, and the per-email
        // bucket that exists for exactly this never saw an attempt.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var email = NewEmail();

        var registered = await RegisterAsync(client, email, source);
        var token = (await registered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        for (var guess = 1; guess <= SignInThrottle.MaxPerEmail; guess++)
        {
            var attempt = await SendAsync(client, HttpMethod.Post, "/api/v1/me/password",
                new { currentPassword = "not it", newPassword = "another good passphrase" },
                source, token);

            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var throttled = await SendAsync(client, HttpMethod.Post, "/api/v1/me/password",
            new { currentPassword = "not it", newPassword = "another good passphrase" },
            source, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task Guessing_the_password_on_delete_runs_out_of_attempts()
    {
        // The same hole on the one action in the feature with no undo after the retention
        // window. MeEndpoints says a stolen session should not be able to destroy an
        // account; an unlimited guessing loop against this route defeated that sentence.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var email = NewEmail();

        var registered = await RegisterAsync(client, email, source);
        var token = (await registered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        for (var guess = 1; guess <= SignInThrottle.MaxPerEmail; guess++)
        {
            var attempt = await SendAsync(client, HttpMethod.Delete, "/api/v1/me",
                new { password = "not it" }, source, token);

            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var throttled = await SendAsync(client, HttpMethod.Delete, "/api/v1/me",
            new { password = "not it" }, source, token);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // And the account is still there, which is the only outcome that matters.
        await using var context = database.NewContext();
        Assert.True(await context.Members
            .AnyAsync(m => m.NormalizedEmail == EmailAddress.Normalize(email)));
    }

    [Fact]
    public async Task Changing_the_password_successfully_costs_nothing_in_either_bucket()
    {
        // The authentication succeeded, so it is not a failed attempt — and a member must
        // not walk away from a successful password change closer to being locked out of
        // sign-in than they started.
        using var factory = Api();
        using var client = factory.CreateClient();
        var source = $"203.0.113.{Random.Shared.Next(1, 254)}";
        var email = NewEmail();

        var registered = await RegisterAsync(client, email, source);
        var token = (await registered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;

        var changed = await SendAsync(client, HttpMethod.Post, "/api/v1/me/password",
            new { currentPassword = Password, newPassword = "a different good passphrase" },
            source, token);

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        await using var context = database.NewContext();
        Assert.Equal(0, await context.SignInAttempts
            .CountAsync(a => a.NormalizedEmail == EmailAddress.Normalize(email)));
    }
}
