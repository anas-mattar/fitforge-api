using System;
using FitForge.Api.Features.Identity;
using FitForge.Domain.Members;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FitForge.Api.Tests;

/// <summary>
/// T019 and T020 — <c>plan.md</c> D2. Hashing is the single most consequential decision in
/// feature 002, which is why it has its own phase, its own gate and this file.
/// </summary>
public class MemberPasswordHasherTests
{
    private const string Password = "correct horse battery staple";

    private static MemberPasswordHasher HasherAt(int iterations)
    {
        var options = Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = iterations,
        });

        return new MemberPasswordHasher(new PasswordHasher<Member>(options));
    }

    // The shipped parameters are expensive on purpose. Tests that only need "a hash"
    // use a low count so the suite stays fast; the two that are about the real numbers
    // say so.
    private static MemberPasswordHasher Cheap() => HasherAt(1_000);

    [Fact]
    public void The_iteration_count_is_the_owasp_floor_for_pbkdf2_hmac_sha512()
    {
        // This constant exists to be raised, never lowered. A test on it means a change
        // is a deliberate edit to two places rather than a quiet tweak to one.
        Assert.Equal(210_000, MemberPasswordHasher.IterationCount);
    }

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hasher = Cheap();
        var member = new Member { PasswordHash = hasher.Hash(Password) };

        var result = hasher.Verify(member, Password);

        Assert.True(result.Succeeded);
        Assert.False(result.Rehashed);
    }

    [Theory]
    [InlineData("wrong password entirely")]
    [InlineData("correct horse battery stapl")]   // one character short
    [InlineData("Correct horse battery staple")]  // one case different
    [InlineData("")]
    public void A_hash_does_not_verify_against_anything_else(string attempt)
    {
        var hasher = Cheap();
        var member = new Member { PasswordHash = hasher.Hash(Password) };

        Assert.False(hasher.Verify(member, attempt).Succeeded);
    }

    [Fact]
    public void The_same_password_hashes_to_two_different_values()
    {
        // The salt is real. Without this, an identical stored value would tell an
        // attacker with a database dump which members share a password.
        var hasher = Cheap();

        Assert.NotEqual(hasher.Hash(Password), hasher.Hash(Password));
    }

    [Fact]
    public void The_stored_value_does_not_contain_the_password()
    {
        // FR-003, stated as bluntly as it can be asserted.
        var hash = Cheap().Hash(Password);

        Assert.DoesNotContain("correct", hash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("staple", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_hash_from_a_lower_iteration_count_verifies_and_is_rewritten()
    {
        // T020, and the reason D2's deferral of Argon2id is reversible rather than a
        // promise. A member hashed under old parameters signs in normally and leaves with
        // a hash under the new ones — no lockout, no migration, no reset email (which
        // this product could not send anyway).
        var old = HasherAt(1_000);
        var member = new Member { PasswordHash = old.Hash(Password) };
        var before = member.PasswordHash;

        var current = HasherAt(2_000);
        var result = current.Verify(member, Password);

        Assert.True(result.Succeeded);
        Assert.True(result.Rehashed);

        // The assertion that matters: the stored value CHANGED. Asserting that a method
        // was called would pass against an implementation that computed a new hash and
        // dropped it.
        Assert.NotEqual(before, member.PasswordHash);

        // And the rewritten value is usable — a rehash that produced a hash the next
        // sign-in rejects would lock the member out on their second visit, which is worse
        // than never rehashing at all.
        Assert.True(current.Verify(member, Password).Succeeded);
    }

    [Fact]
    public void A_failed_verification_never_rewrites_the_stored_hash()
    {
        var hasher = HasherAt(2_000);
        var member = new Member { PasswordHash = HasherAt(1_000).Hash(Password) };
        var before = member.PasswordHash;

        var result = hasher.Verify(member, "not the password");

        Assert.False(result.Succeeded);
        Assert.False(result.Rehashed);
        Assert.Equal(before, member.PasswordHash);
    }

    [Fact]
    public void The_decoy_path_does_the_same_work_as_a_real_verification()
    {
        // plan.md D6. Asserted through work done, not through wall-clock timing: a timing
        // assertion in CI is a flaky test, not a security control.
        //
        // The counting hasher records every call. A VerifyDecoy that returned early —
        // the plausible "optimization" someone makes later — does no verification, and
        // this test catches it.
        var counting = new CountingPasswordHasher(new PasswordHasher<Member>(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 })));
        var hasher = new MemberPasswordHasher(counting);

        var afterConstruction = counting.VerifyCalls;
        hasher.VerifyDecoy("whatever was submitted");

        Assert.Equal(afterConstruction + 1, counting.VerifyCalls);
    }

    [Fact]
    public void The_decoy_is_hashed_once_at_construction_not_per_call()
    {
        // The stretch is deliberate and expensive. Paying it per unknown-address sign-in
        // would turn the timing defence into a denial-of-service amplifier.
        var counting = new CountingPasswordHasher(new PasswordHasher<Member>(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 })));
        var hasher = new MemberPasswordHasher(counting);

        Assert.Equal(1, counting.HashCalls);

        hasher.VerifyDecoy("a");
        hasher.VerifyDecoy("b");

        Assert.Equal(1, counting.HashCalls);
    }

    [Fact]
    public void The_decoy_never_reports_a_match()
    {
        // If the decoy's plaintext were guessable, the timing defence would become a
        // correctness bug. It is 32 random bytes; nothing a caller submits matches it.
        // VerifyDecoy returns void precisely so no caller can branch on it — this test
        // asserts the property underneath that choice.
        var counting = new CountingPasswordHasher(new PasswordHasher<Member>(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 })));
        var hasher = new MemberPasswordHasher(counting);

        hasher.VerifyDecoy("");
        hasher.VerifyDecoy("password");
        hasher.VerifyDecoy(new string('a', 64));

        Assert.All(counting.VerifyResults, r => Assert.Equal(PasswordVerificationResult.Failed, r));
    }

    [Fact]
    public void Null_arguments_are_refused_rather_than_hashed()
    {
        var hasher = Cheap();
        var member = new Member { PasswordHash = hasher.Hash(Password) };

        Assert.Throws<ArgumentNullException>(() => hasher.Hash(null!));
        Assert.Throws<ArgumentNullException>(() => hasher.Verify(null!, Password));
        Assert.Throws<ArgumentNullException>(() => hasher.Verify(member, null!));
        Assert.Throws<ArgumentNullException>(() => hasher.VerifyDecoy(null!));
    }

    /// <summary>
    /// Wraps the real hasher and counts. Not a stub — the underlying work still happens,
    /// so a test asserting "verification occurred" is asserting about real verification.
    /// </summary>
    private sealed class CountingPasswordHasher(IPasswordHasher<Member> inner) : IPasswordHasher<Member>
    {
        public int HashCalls { get; private set; }

        public int VerifyCalls { get; private set; }

        public List<PasswordVerificationResult> VerifyResults { get; } = [];

        public string HashPassword(Member user, string password)
        {
            HashCalls++;
            return inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(Member user, string hashedPassword, string providedPassword)
        {
            VerifyCalls++;
            var result = inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
            VerifyResults.Add(result);
            return result;
        }
    }
}
