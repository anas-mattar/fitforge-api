using FitForge.Api.Features.Health;
using FitForge.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// RFC 9457 for every failure (ADR-001 §4.4). AddProblemDetails supplies the writer, the
// exception handler maps domain failures, and UseStatusCodePages covers the responses no
// handler produced a body for — 404 on an unmatched route, 405 on a wrong method.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

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
