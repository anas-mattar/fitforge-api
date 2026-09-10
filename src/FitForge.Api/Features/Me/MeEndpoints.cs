using System.Text.Json;
using FitForge.Api.Features.Identity;
using FitForge.Api.Hosting.Authentication;
using FitForge.Domain.Members;
using FitForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitForge.Api.Features.Me;

/// <summary>
/// Everything a member can read or change about themselves — <c>contracts/member.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every path here is <c>/me</c>-shaped, and that is the feature's answer to training
/// invariant 2.</b> There is no route parameter, no query parameter and no body field by
/// which a caller names <i>which</i> member: the authenticated member comes from
/// <see cref="CurrentMember"/> and from nothing else (<c>plan.md</c> D7).
/// </para>
/// <para>
/// The difference from a filter-based design is what a reviewer has to do. Here they look
/// for a path that takes a member identifier and find none. With filters they would have
/// to read every query and trust that the next one written is like the last. `T054`
/// turns that inspection into a test, so the property survives the reviewer's attention
/// span.
/// </para>
/// </remarks>
public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/me").RequireAuthorization();

        group.MapGet("/", GetAsync).WithName("GetMe");
        group.MapPatch("/preferences", UpdatePreferencesAsync).WithName("UpdatePreferences");
        group.MapPost("/password", ChangePasswordAsync).WithName("ChangePassword");
        group.MapDelete("/", DeleteAsync).WithName("DeleteMe");

        return app;
    }

    // ---- §1 read -------------------------------------------------------------------

    private static async Task<IResult> GetAsync(
        CurrentMember current,
        FitForgeDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        var profile = await db.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.MemberId == current.Member.Id, cancellationToken);

        return Results.Ok(new
        {
            member = MemberSummary.From(current.Member),
            profile = new
            {
                birthYear = profile?.BirthYear,
                sex = profile?.Sex?.ToString(),
                // Centimetres, always. The caller converts for display and never sends a
                // converted value back (invariant 4, FR-011).
                heightCm = profile?.HeightCm,
            },
        });
    }

    // ---- §2 preferences -------------------------------------------------------------

    private static async Task<IResult> UpdatePreferencesAsync(
        JsonElement request,
        CurrentMember current,
        FitForgeDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        // A JsonElement rather than a record, because the contract distinguishes an
        // ABSENT field (leave it alone) from a null one (rejected). Bound to a record of
        // nullable properties the two collapse into the same value, and "absent means
        // unchanged" quietly becomes "absent means clear it".
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var member = await db.Members.FirstAsync(m => m.Id == current.Member.Id, cancellationToken);

        if (TryRead(request, "units", errors, out var units))
        {
            if (Enum.TryParse<UnitPreference>(units, ignoreCase: false, out var parsed))
            {
                // Display only. Nothing stored is rewritten — FR-011, invariant 4, VI-028,
                // and the property T055 exists to keep true.
                member.Units = parsed;
            }
            else
            {
                errors["units"] = ["Choose Metric or Imperial."];
            }
        }

        if (TryRead(request, "goal", errors, out var goal))
        {
            if (Enum.TryParse<Goal>(goal, ignoreCase: false, out var parsed))
            {
                member.Goal = parsed;
            }
            else
            {
                errors["goal"] = ["That goal is not one of the options."];
            }
        }

        if (TryRead(request, "experience", errors, out var experience))
        {
            if (Enum.TryParse<ExperienceLevel>(experience, ignoreCase: false, out var parsed))
            {
                member.Experience = parsed;
            }
            else
            {
                errors["experience"] = ["That experience level is not one of the options."];
            }
        }

        if (TryRead(request, "timeZone", errors, out var timeZone))
        {
            if (Resolves(timeZone!))
            {
                member.TimeZone = timeZone!;
            }
            else
            {
                // Never a silent fall back to UTC. A member whose streak is counted in
                // the wrong zone should be told, not quietly mis-served — the copy at
                // VI-023 promises them weeks are counted in this zone (plan.md D10).
                errors["timeZone"] = ["That time zone is not recognised."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(
                errors,
                type: "/problems/validation",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        member.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        member.UpdatedBy = member.PublicId.ToString();
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(MemberSummary.From(member));
    }

    // ---- §3 password ----------------------------------------------------------------

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        CurrentMember current,
        FitForgeDbContext db,
        MemberPasswordHasher hasher,
        SessionService sessions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(current);

        var member = await db.Members
            .AsNoTracking()
            .FirstAsync(m => m.Id == current.Member.Id, cancellationToken);

        var observedHash = member.PasswordHash;

        if (!hasher.Verify(member, request.CurrentPassword ?? string.Empty).Succeeded)
        {
            // Deliberately distinguishable from sign-in's message, and the difference is
            // safe: the caller has already proved they are this member, so naming the
            // wrong field leaks nothing and saves them a guess.
            return Results.Problem(
                type: "/problems/invalid-credentials",
                title: "Your current password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var newPassword = request.NewPassword ?? string.Empty;
        var violation = PasswordPolicy.Check(newPassword, member.Email);

        if (violation is not PasswordPolicyViolation.None)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["newPassword"] = [Describe(violation)],
                },
                type: "/problems/validation",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        if (hasher.Verify(member, newPassword).Succeeded)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["newPassword"] = ["Choose a password you have not just used."],
                },
                type: "/problems/validation",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        // Compare-and-swap on the hash we verified against. Two concurrent changes both
        // verify the ORIGINAL password, so a plain update would let the second silently
        // overwrite the first and leave the member holding a password nobody chose
        // knowingly. Matching on the observed hash means exactly one wins and the other
        // is told (T060). No rowversion column, and so no migration, is needed for it.
        var updated = await db.Members
            .Where(m => m.Id == member.Id && m.PasswordHash == observedHash)
            .ExecuteUpdateAsync(
                u => u
                    .SetProperty(m => m.PasswordHash, hasher.Hash(newPassword))
                    .SetProperty(m => m.UpdatedAtUtc, clock.GetUtcNow().UtcDateTime)
                    .SetProperty(m => m.UpdatedBy, member.PublicId.ToString()),
                cancellationToken);

        if (updated == 0)
        {
            return Results.Problem(
                type: "/problems/conflict",
                title: "Your password was changed elsewhere. Try again.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // FR-013: every OTHER session. The tab in front of the member survives — signing
        // them out of the one that just proved the current password would make changing
        // a password feel like a punishment.
        await sessions.RevokeAllExceptAsync(member.Id, current.Token, cancellationToken);

        return Results.NoContent();
    }

    // ---- §4 delete -------------------------------------------------------------------

    private static async Task<IResult> DeleteAsync(
        // Explicit, because Minimal APIs will not INFER a body for DELETE. The body is
        // not optional here: contracts/member.md §4 requires deletion to re-authenticate,
        // and the alternative shapes - a password in the query string, or a POST
        // /me/delete - would either put a credential in a URL (never) or change an
        // approved contract to avoid one attribute.
        [Microsoft.AspNetCore.Mvc.FromBody] DeleteMeRequest request,
        CurrentMember current,
        FitForgeDbContext db,
        MemberPasswordHasher hasher,
        SessionService sessions,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(current);

        var member = await db.Members.FirstAsync(m => m.Id == current.Member.Id, cancellationToken);

        // Deletion re-authenticates. A stolen session should not be able to destroy an
        // account — of everything in this feature, this is the action with no undo after
        // the retention window.
        if (!hasher.Verify(member, request.Password ?? string.Empty).Succeeded)
        {
            return Results.Problem(
                type: "/problems/invalid-credentials",
                title: "Your password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var actor = member.PublicId.ToString();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        member.IsDeleted = true;
        member.DeletedAtUtc = now;
        member.DeletedBy = actor;

        // Everything they own. Today that is one profile; every later feature that adds a
        // member-owned table extends this, and T066's purge-completeness test is what
        // makes forgetting fail the gate rather than orphan personal data.
        await db.Profiles
            .Where(p => p.MemberId == member.Id)
            .ExecuteUpdateAsync(
                u => u
                    .SetProperty(p => p.IsDeleted, true)
                    .SetProperty(p => p.DeletedAtUtc, now)
                    .SetProperty(p => p.DeletedBy, actor),
                cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // Every session, the presented one included (FR-014). Signing out only the other
        // tabs would leave the member using an account that no longer exists.
        await sessions.RevokeAllAsync(member.Id, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Results.NoContent();
    }

    // ---- helpers ---------------------------------------------------------------------

    /// <summary>
    /// Reads an optional string field: absent leaves it alone, explicit null is refused.
    /// </summary>
    private static bool TryRead(
        JsonElement request,
        string name,
        Dictionary<string, string[]> errors,
        out string? value)
    {
        value = null;

        if (request.ValueKind is not JsonValueKind.Object ||
            !request.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.Null)
        {
            // None of these four is clearable: each has a default and a member always has
            // one. Accepting null would mean "unset my time zone", which has no meaning
            // the product can honour.
            errors[name] = ["This cannot be cleared."];
            return false;
        }

        if (property.ValueKind is not JsonValueKind.String)
        {
            errors[name] = ["Expected a value."];
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    /// <summary>
    /// Whether the host can resolve an IANA identifier. .NET resolves IANA names on
    /// Windows and Linux alike through ICU, so this is the same answer on both.
    /// </summary>
    private static bool Resolves(string timeZone)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            // The zone exists in the database but its data is corrupt. Refusing is right:
            // a zone that cannot compute a boundary is no more usable than one that does
            // not exist, and silently accepting it would move the failure to whichever
            // later feature first counts a week.
            return false;
        }
    }

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

/// <summary>Request body for <c>POST /api/v1/me/password</c>.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Request body for <c>DELETE /api/v1/me</c>.</summary>
public sealed record DeleteMeRequest(string? Password);
