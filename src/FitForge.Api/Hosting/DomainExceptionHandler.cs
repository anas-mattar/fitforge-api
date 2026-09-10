using FitForge.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FitForge.Api.Hosting;

/// <summary>
/// The single place where a domain failure becomes an HTTP status code (ADR-001 §4.4).
/// </summary>
/// <remarks>
/// Every failure leaves this API as <c>application/problem+json</c> (RFC 9457) — including
/// the ones nobody wrote code for. Outside Development the document carries no exception
/// message and no stack trace: an error response is read by whoever can reach the endpoint,
/// not only by the developer who caused it.
/// </remarks>
internal sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            DomainRuleViolationException => (StatusCodes.Status422UnprocessableEntity, "Request violates a domain rule"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        var problemDetails = new ProblemDetails
        {
            Status = status,
            Title = title,
            // A domain exception's message is written for a caller and is safe to return.
            // Anything else may carry internal detail, so it is disclosed in Development only.
            Detail = exception is DomainException || environment.IsDevelopment()
                ? exception.Message
                : null,
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
