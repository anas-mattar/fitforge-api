using System.Globalization;
using FitForge.Api.Hosting.Authentication;
using FitForge.Domain.Members;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Features.Identity;

/// <summary>
/// The authentication surface — <c>contracts/auth.md</c>.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>
    /// The single sign-in failure a caller ever sees. One message, one status, for an
    /// unknown address, a wrong password and a soft-deleted member alike (FR-004; the
    /// wording is VI-012, verbatim).
    /// </summary>
    private const string InvalidCredentials = "Email or password is incorrect.";

    /// <summary>
    /// The header the BFF forwards the caller's address in (<c>contracts/auth.md</c> §6).
    /// </summary>
    private const string ForwardedFor = "X-Forwarded-For";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth");

        group.MapPost("/register", RegisterAsync).WithName("Register");
        group.MapPost("/sign-in", SignInAsync).WithName("SignIn");
        group.MapPost("/sign-out", SignOutAsync).RequireAuthorization().WithName("SignOut");

        // contracts/auth.md §5. The 401 for an unresolvable token is produced by the
        // authorization pipeline, not here: by the time this delegate runs, the session
        // resolved. That is the point of doing it in a handler — the endpoint has no
        // "is this valid" branch to get wrong.
        group.MapGet("/session", (CurrentMember current) => Results.Ok(new
        {
            member = MemberSummary.From(current.Member),
            expiresAtUtc = current.ExpiresAtUtc,
        }))
        .RequireAuthorization()
        .WithName("GetSession");

        return app;
    }

    // ---- §2 register ----------------------------------------------------------------

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        FitForgeDbContext db,
        MemberPasswordHasher hasher,
        SessionService sessions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = EmailAddress.ForDisplay(request.Email ?? string.Empty);
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (email.Length is 0 or > 254 || !email.Contains('@', StringComparison.Ordinal))
        {
            errors["email"] = ["Enter an email address."];
        }

        if (displayName.Length is 0 or > 60)
        {
            errors["displayName"] = ["Enter a name between 1 and 60 characters."];
        }

        var violation = PasswordPolicy.Check(password, email);
        if (violation is not PasswordPolicyViolation.None)
        {
            errors["password"] = [Describe(violation)];
        }

        if (errors.Count > 0)
        {
            // 422, not the 400 Results.ValidationProblem defaults to: contracts/auth.md
            // §2 fixes the status, and the BFF branches on it. Caught by a test rather
            // than by review — the default is quietly plausible.
            return Results.ValidationProblem(
                errors,
                type: "/problems/validation",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var normalized = EmailAddress.Normalize(email);
        var now = clock.GetUtcNow().UtcDateTime;

        var member = new Member
        {
            Email = email,
            NormalizedEmail = normalized,
            PasswordHash = hasher.Hash(password),
            DisplayName = displayName,
            CreatedAtUtc = now,
            CreatedBy = "self",
        };

        // Created with the member and in the same SaveChanges: data-model.md requires
        // that no read path handle a missing profile, and a second call that could fail
        // independently would be exactly such a path.
        member.Profile = new Profile
        {
            CreatedAtUtc = now,
            CreatedBy = "self",
        };

        db.Members.Add(member);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // UQ_Member_NormalizedEmail is the arbiter, not a prior SELECT. Two
            // simultaneous registrations of one address both pass a check-then-insert;
            // only one passes the unique index. A pre-check would produce a nicer
            // message more often and would still be wrong under a race.
            //
            // Rethrow anything that is NOT the duplicate: a save failing for some other
            // reason must not be reported to a visitor as "that email is taken", which
            // would be both wrong and, since it reveals nothing true, quietly misleading.
            if (!await ExistsAsync(db, normalized, cancellationToken))
            {
                throw;
            }

            return EmailTaken();
        }

        var token = await sessions.IssueAsync(member, cancellationToken);

        return Results.Created($"/api/v1/members/{member.PublicId}", new
        {
            token,
            expiresAtUtc = now + SessionService.Lifetime,
            member = MemberSummary.From(member),
        });
    }

    // ---- §3 sign in -----------------------------------------------------------------

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext http,
        FitForgeDbContext db,
        MemberPasswordHasher hasher,
        SessionService sessions,
        SignInThrottle throttle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        var normalized = EmailAddress.Normalize(request.Email ?? string.Empty);
        var password = request.Password ?? string.Empty;
        var source = http.Request.Headers[ForwardedFor].ToString();

        // Before any hashing. A throttle applied after the deliberate 210,000-iteration
        // stretch would amplify a denial of service rather than blunt one.
        if (await throttle.RetryAfterAsync(normalized, source, cancellationToken) is { } wait)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);

            return Results.Problem(
                type: "/problems/too-many-attempts",
                title: "Too many attempts. Try again shortly.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var member = await db.Members
            .FirstOrDefaultAsync(m => m.NormalizedEmail == normalized, cancellationToken);

        if (member is null)
        {
            // The decoy. Without it, "no such member" returns in the time of an index
            // miss while "wrong password" takes the full stretch — a difference a
            // stopwatch reads as an existence oracle (plan.md D6).
            hasher.VerifyDecoy(password);

            await throttle.RecordFailureAsync(normalized, source, cancellationToken);
            return InvalidCredentialsProblem();
        }

        var verification = hasher.Verify(member, password);

        if (!verification.Succeeded)
        {
            await throttle.RecordFailureAsync(normalized, source, cancellationToken);
            return InvalidCredentialsProblem();
        }

        if (verification.Rehashed)
        {
            // The stored hash was upgraded inside Verify; this is the save. It happens on
            // the one path where the plaintext exists, which is what makes plan.md D2's
            // deferral of Argon2id reversible rather than permanent.
            member.UpdatedAtUtc = DateTime.UtcNow;
            member.UpdatedBy = member.PublicId.ToString();
            await db.SaveChangesAsync(cancellationToken);
        }

        await throttle.ClearEmailAsync(normalized, cancellationToken);

        var token = await sessions.IssueAsync(member, cancellationToken);
        var resolved = await sessions.ResolveAsync(token, cancellationToken);

        return Results.Ok(new
        {
            token,
            // Read back rather than recomputed: the response carries what was stored, not
            // a second calculation of it that could drift from the first.
            expiresAtUtc = resolved!.ExpiresAtUtc,
            member = MemberSummary.From(member),
        });
    }

    // ---- §4 sign out ----------------------------------------------------------------

    private static async Task<IResult> SignOutAsync(
        CurrentMember current,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Server-side, not merely a cleared cookie (FR-006). Idempotent by construction:
        // revoking an already-revoked session updates nothing and still returns 204.
        await sessions.RevokeAsync(current.Token, cancellationToken);

        return Results.NoContent();
    }

    // ---- helpers ---------------------------------------------------------------------

    private static Task<bool> ExistsAsync(
        FitForgeDbContext db,
        string normalizedEmail,
        CancellationToken cancellationToken) =>
        db.Members
            .IgnoreQueryFilters()
            .AnyAsync(m => m.NormalizedEmail == normalizedEmail, cancellationToken);

    private static IResult EmailTaken() => Results.Problem(
        type: "/problems/email-taken",
        title: "That email is already registered.",
        statusCode: StatusCodes.Status409Conflict);

    private static IResult InvalidCredentialsProblem() => Results.Problem(
        type: "/problems/invalid-credentials",
        title: InvalidCredentials,
        statusCode: StatusCodes.Status401Unauthorized);

    private static string Describe(PasswordPolicyViolation violation) => violation switch
    {
        PasswordPolicyViolation.TooShort =>
            $"Use at least {PasswordPolicy.MinimumLength} characters.",
        PasswordPolicyViolation.TooLong =>
            $"Use at most {PasswordPolicy.MaximumLength} characters.",
        PasswordPolicyViolation.SameAsEmail =>
            "Your password cannot be your email address.",
        _ => "That password cannot be used.",
    };
}

/// <summary>Request body for <c>POST /api/v1/auth/register</c>.</summary>
public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);

/// <summary>Request body for <c>POST /api/v1/auth/sign-in</c>.</summary>
public sealed record SignInRequest(string? Email, string? Password);

/// <summary>
/// The member shape every contract in this feature returns (<c>contracts/auth.md</c> §8).
/// </summary>
/// <remarks>
/// Enum values go over the wire as <b>names</b>, not numbers: the column is a
/// <c>TINYINT</c> for storage, and a member-facing label should be renameable without a
/// migration. <c>memberSince</c> is an instant — formatting it as <c>19 Aug 2026</c>
/// (VI-024) is the browser's job, because only the browser knows the member's locale.
/// </remarks>
public sealed record MemberSummary(
    Guid PublicId,
    string Email,
    string DisplayName,
    DateTime MemberSince,
    string Units,
    string TimeZone,
    string Goal,
    string Experience)
{
    public static MemberSummary From(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new MemberSummary(
            member.PublicId,
            member.Email,
            member.DisplayName,
            member.CreatedAtUtc,
            member.Units.ToString(),
            member.TimeZone,
            member.Goal.ToString(),
            member.Experience.ToString());
    }
}
