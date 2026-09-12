using System.Globalization;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// The one 429 the product returns, in the one place it is built.
/// </summary>
/// <remarks>
/// Four endpoints now refuse on the throttle — sign-in, register, change-password and
/// delete-account — and a member must not be able to tell them apart by the shape of the
/// refusal. Four copies of this would drift, and the first one to drift would be an oracle.
/// </remarks>
internal static class ThrottleResults
{
    internal static IResult TooManyAttempts(HttpContext http, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(http);

        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds))
            .ToString(CultureInfo.InvariantCulture);

        return Results.Problem(
            type: "/problems/too-many-attempts",
            title: "Too many attempts. Try again shortly.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }
}
