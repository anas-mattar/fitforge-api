using FitForge.Api.Features.Health;
using FitForge.Api.Features.Identity;
using FitForge.Api.Features.Me;
using FitForge.Api.Hosting;
using FitForge.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// The host asks for persistence; it does not configure it (ADR-001 §4.3). The connection
// string, the provider and the readiness check are all decided inside Infrastructure, so
// this project never names any of them.
builder.Services.AddFitForgePersistence(builder.Configuration);

// RFC 9457 for every failure (ADR-001 §4.4). AddProblemDetails supplies the writer, the
// exception handler maps domain failures, and UseStatusCodePages covers the responses no
// handler produced a body for — 404 on an unmatched route, 405 on a wrong method.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

// Password hashing, and only password hashing — deliberately not AddIdentity (see the
// extension). Registered at startup because the decoy hash that keeps an unknown address
// indistinguishable from a wrong password is computed once, here, not per request.
builder.Services.AddFitForgePasswordHashing();

// Sessions and the bearer scheme that resolves them (plan.md D3). The BFF is the only
// caller that ever sets the header; the browser cannot read the cookie it comes from.
builder.Services.AddFitForgeSessions();

// The sign-in throttle, and the one secret it needs. Validated at startup for the same
// reason the connection string is: a configuration error should stop the application,
// not surface as a member's sign-in behaving oddly.
builder.Services.AddFitForgeSignInThrottle(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();

// Order matters and is not incidental: authentication resolves the session and populates
// CurrentMember, authorization then decides. Swapping them would make every
// RequireAuthorization() endpoint reject a valid session.
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapMeEndpoints();

app.Run();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host this application in
/// tests — top-level statements otherwise compile to an internal type.
/// </summary>
public partial class Program;
