using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace FitForge.Api.Tests;

/// <summary>
/// The part of <c>GET /api/v1/auth/session</c> that can be proved without a database:
/// every shape of missing or malformed credential is refused identically, and none of
/// them reaches persistence.
/// </summary>
/// <remarks>
/// <para>
/// <c>contracts/auth.md</c> §5. The cases that require a stored session — a live token, an
/// expired one, a revoked one, and one belonging to a soft-deleted member — are T029 and
/// are <b>not</b> covered here. See the open decision in <c>tasks.md</c>: this repository
/// has no test database, on this host or in CI.
/// </para>
/// <para>
/// What follows is therefore a partial test, and it is named as one rather than left to
/// look like coverage it is not.
/// </para>
/// </remarks>
public class SessionEndpointTests
{
    [Fact]
    public async Task No_authorization_header_is_401()
    {
        using var factory = new FitForgeApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Basic", "dXNlcjpwYXNz")]
    [InlineData("Bearer", "")]
    [InlineData("Bearer", "   ")]
    public async Task A_credential_that_is_not_a_bearer_token_is_the_same_401(string scheme, string parameter)
    {
        // Same status for every shape. A 400 for "malformed" and a 401 for "wrong" would
        // let a caller separate the two, and the contract requires one answer.
        using var factory = new FitForgeApiFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, parameter);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_never_reaches_the_database()
    {
        // Load-bearing, and the reason this test exists at all: the factory registers a
        // connection string pointing nowhere. If a missing header caused a lookup, this
        // would surface as a connection failure — a 500, or a hang — rather than a 401.
        //
        // It matters beyond tidiness: an unauthenticated request that touches the
        // database is a denial-of-service surface reachable with no credential.
        using var factory = new FitForgeApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // 401, not 500. A provider exception would surface as the latter, and the
        // distinction is the whole assertion.
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task The_401_is_a_problem_document_like_every_other_failure()
    {
        // ADR-001 §4.4: no framework default page is reachable, 401 included. The first
        // draft of the test above asserted an EMPTY body and failed — the body was a
        // problem document, which is what the architecture requires. Recorded because the
        // failure was the test being wrong about the rule, not the code breaking it, and
        // the corrected assertion is the more useful one.
        using var factory = new FitForgeApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/session");

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
