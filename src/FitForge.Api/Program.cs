using FitForge.Api.Features.Health;
using FitForge.Api.Features.Identity;
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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseHttpsRedirection();

app.MapHealthEndpoints();

app.Run();

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can host this application in
/// tests — top-level statements otherwise compile to an internal type.
/// </summary>
public partial class Program;
