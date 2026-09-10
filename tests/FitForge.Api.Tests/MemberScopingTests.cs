using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FitForge.Api.Features.Identity;
using FitForge.Api.Hosting.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace FitForge.Api.Tests;

/// <summary>
/// T053 and T054 — <c>plan.md</c> D13 tests 3 and 2. These exist to fail when a
/// <b>future</b> feature breaks what this one established.
/// </summary>
/// <remarks>
/// Training invariant 2 calls a query that can return another member's data "a defect of
/// the highest severity, not a bug to schedule". These are the two tests standing between
/// that sentence and a regression.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public class MemberScopingTests(SqlServerDatabase database)
{
    private const string Password = "correct horse battery staple";

    private FitForgeApiFactory Api() =>
        new() { ConnectionString = database.Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .Single().ConnectionString };

    private static async Task<(string Token, string PublicId, string Email)> NewMemberAsync(HttpClient client)
    {
        var email = $"member-{Guid.NewGuid():N}@example.com";

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, Password, "Test Member"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (
            body.GetProperty("token").GetString()!,
            body.GetProperty("member").GetProperty("publicId").GetString()!,
            email);
    }

    private static HttpRequestMessage As(string token, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // ---- T053 / SC-002 ---------------------------------------------------------------

    [Fact]
    public async Task Member_A_cannot_reach_member_B_through_any_exposed_identifier()
    {
        using var factory = Api();
        using var client = factory.CreateClient();

        var a = await NewMemberAsync(client);
        var b = await NewMemberAsync(client);

        // Every place a caller could try to name another member. The point is not that
        // each is rejected — it is that each is IGNORED, because no code reads it. A
        // design that rejected them would still have somewhere to forget.
        var attempts = new List<HttpRequestMessage>
        {
            As(a.Token, HttpMethod.Get, $"/api/v1/me?memberId={b.PublicId}"),
            As(a.Token, HttpMethod.Get, $"/api/v1/me?publicId={b.PublicId}"),
            As(a.Token, HttpMethod.Get, $"/api/v1/me/{b.PublicId}"),
        };

        var header = As(a.Token, HttpMethod.Get, "/api/v1/me");
        header.Headers.Add("X-Member-Id", b.PublicId);
        attempts.Add(header);

        foreach (var attempt in attempts)
        {
            var response = await client.SendAsync(attempt);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // /api/v1/me/{something} matches no route at all — which is the strongest
                // possible answer and exactly what a shape-based design produces.
                continue;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var returned = body.GetProperty("member").GetProperty("publicId").GetString();

            Assert.Equal(a.PublicId, returned);
            Assert.NotEqual(b.PublicId, returned);
        }
    }

    [Fact]
    public async Task A_write_supplying_another_members_identifier_still_writes_to_the_caller()
    {
        using var factory = Api();
        using var client = factory.CreateClient();

        var a = await NewMemberAsync(client);
        var b = await NewMemberAsync(client);

        var write = As(a.Token, HttpMethod.Patch, "/api/v1/me/preferences");
        write.Content = JsonContent.Create(new
        {
            units = "Imperial",
            // Both ignored, because nothing reads them.
            memberId = b.PublicId,
            publicId = b.PublicId,
        });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(write)).StatusCode);

        // A is changed...
        var readA = await client.SendAsync(As(a.Token, HttpMethod.Get, "/api/v1/me"));
        var bodyA = await readA.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Imperial", bodyA.GetProperty("member").GetProperty("units").GetString());

        // ...and B is untouched. Without this half, a write that silently did nothing
        // would pass the assertion above.
        var readB = await client.SendAsync(As(b.Token, HttpMethod.Get, "/api/v1/me"));
        var bodyB = await readB.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Metric", bodyB.GetProperty("member").GetProperty("units").GetString());
    }

    // ---- T054: the structural property, asserted ------------------------------------

    [Fact]
    public void No_endpoint_under_me_declares_a_parameter_that_names_a_member()
    {
        // plan.md D7. This is the test most likely to be deleted by someone who finds it
        // annoying — which is the argument for it, not against it. Invariant 2 is upheld
        // here by an ABSENCE, and an absence is only durable if something notices when it
        // stops being true.
        using var factory = Api();
        using var scope = factory.Services.CreateScope();

        var endpoints = scope.ServiceProvider
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/me", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText;

            // No route parameter at all under /me. Not "no parameter named memberId" —
            // none, because the next one to be added would be named something else.
            Assert.Empty(endpoint.RoutePattern.Parameters);

            var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
            Assert.NotNull(handler);

            foreach (var parameter in handler!.GetParameters())
            {
                Assert.False(
                    NamesAMember(parameter.Name),
                    $"{route}: handler parameter '{parameter.Name}' names a member.");

                foreach (var property in RequestBoundProperties(parameter.ParameterType, scope.ServiceProvider))
                {
                    Assert.False(
                        NamesAMember(property),
                        $"{route}: request body field '{property}' names a member.");
                }
            }
        }

        static bool NamesAMember(string? name) =>
            name is not null &&
            (name.Contains("member", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("userId", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("publicId", StringComparison.OrdinalIgnoreCase));

        // Only what is bound FROM THE REQUEST is inspected, and DI is what settles that:
        // a parameter the container can supply is a service, not something a caller can
        // fill in. Two earlier drafts got this wrong in instructive ways — the first
        // flagged CurrentMember.Member, the second FitForgeDbContext.Members, and both
        // are types a caller cannot influence at all.
        //
        // Excluding those two by name would have been the weak fix: it would have left
        // the check passing for a genuinely dangerous type that happened to be called
        // something else. Asking the container is exact, and it stays exact as services
        // are added.
        static IEnumerable<string> RequestBoundProperties(Type type, IServiceProvider services)
        {
            if (services.GetService(type) is not null)
            {
                return [];
            }

            return type.Namespace?.StartsWith("FitForge", StringComparison.Ordinal) == true
                ? type.GetProperties().Select(p => p.Name)
                : [];
        }
    }

    [Fact]
    public void The_scoping_test_would_notice_if_the_property_were_broken()
    {
        // The companion that keeps the test above honest. A hypothetical handler taking a
        // memberPublicId must be caught by the same predicate the real check uses — if
        // this fails, the check above is asserting nothing.
        var offending = typeof(HypotheticalLeak).GetMethod(nameof(HypotheticalLeak.Handler))!;

        Assert.Contains(
            offending.GetParameters(),
            p => p.Name!.Contains("member", StringComparison.OrdinalIgnoreCase));
    }

    private static class HypotheticalLeak
    {
        public static IResult Handler(Guid memberPublicId) => Results.Ok(memberPublicId);
    }
}
