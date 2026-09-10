using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FitForge.Api.Hosting;
using FitForge.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FitForge.Api.Tests;

/// <summary>
/// ADR-001 §4.4: every failure leaves this API as <c>application/problem+json</c>, and
/// outside Development it carries no internal detail.
/// </summary>
public class ProblemDetailsTests : IDisposable
{
    private readonly FitForgeApiFactory _factory = new();
    private readonly HttpClient _client;

    public ProblemDetailsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Unmatched_route_returns_404_as_problem_json()
    {
        var response = await _client.GetAsync("/no-such-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wrong_method_returns_405_as_problem_json()
    {
        var response = await _client.PostAsync("/health/live", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(typeof(NotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(DomainRuleViolationException), StatusCodes.Status422UnprocessableEntity)]
    public async Task Domain_failures_map_to_their_declared_status(System.Type exceptionType, int expectedStatus)
    {
        var exception = (Exception)System.Activator.CreateInstance(exceptionType, "the message")!;

        var (status, _) = await HandleAsync(exception, isDevelopment: false);

        Assert.Equal(expectedStatus, status);
    }

    [Fact]
    public async Task A_domain_message_is_returned_because_it_is_written_for_the_caller()
    {
        var (_, body) = await HandleAsync(new DomainRuleViolationException("A session cannot be started twice."), isDevelopment: false);

        Assert.Equal("A session cannot be started twice.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_unexpected_failure_is_500_and_discloses_nothing_outside_development()
    {
        var secret = "Login failed for user 'sa' on server db-prod-01";

        var (status, body) = await HandleAsync(new InvalidOperationException(secret), isDevelopment: false);

        Assert.Equal(StatusCodes.Status500InternalServerError, status);

        // The whole document must not carry the message, the exception type or a stack
        // trace. Asserting on the raw JSON rather than one property is deliberate: this
        // is a disclosure test, and a leak can appear in a field nobody thought of.
        var raw = body.GetRawText();
        Assert.DoesNotContain(secret, raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", raw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("at FitForge", raw, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_failure_does_disclose_in_development()
    {
        var (_, body) = await HandleAsync(new InvalidOperationException("boom"), isDevelopment: true);

        Assert.Equal("boom", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Runs the handler over a real <see cref="IProblemDetailsService"/> and returns what
    /// it wrote. Exercised directly rather than through a fake throwing endpoint, so the
    /// application ships no route that exists only for tests.
    /// </summary>
    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception, bool isDevelopment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment(isDevelopment));
        services.AddTransient<DomainExceptionHandler>();

        using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/anything";
        context.Request.Headers.Accept = "application/json";

        var handled = await provider.GetRequiredService<DomainExceptionHandler>()
            .TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled, "The handler must handle every exception — an unhandled one becomes framework HTML.");

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    private sealed class StubEnvironment(bool isDevelopment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = isDevelopment ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "FitForge.Api";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
