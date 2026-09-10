using FitForge.Domain.Members;

namespace FitForge.Domain.Tests;

/// <summary>
/// FR-002 and <c>plan.md</c> D1. A pure function over two strings, so these run with no
/// host and no database.
/// </summary>
public class PasswordPolicyTests
{
    private const string Email = "member@example.com";

    [Theory]
    [InlineData("123456789")]        // 9 — one short
    [InlineData("")]
    [InlineData("short")]
    public void Shorter_than_ten_characters_is_refused(string password)
    {
        Assert.Equal(PasswordPolicyViolation.TooShort, PasswordPolicy.Check(password, Email));
    }

    [Fact]
    public void Exactly_ten_characters_is_accepted()
    {
        // The boundary, explicitly: FR-002 says "at least 10", so 10 passes. An
        // off-by-one here locks out every member who chose the shortest legal password.
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("1234567890", Email));
    }

    [Fact]
    public void Two_hundred_and_fifty_six_characters_is_accepted_and_two_hundred_and_fifty_seven_is_not()
    {
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check(new string('a', 256), Email));
        Assert.Equal(PasswordPolicyViolation.TooLong, PasswordPolicy.Check(new string('a', 257), Email));
    }

    [Theory]
    [InlineData("member@example.com")]
    [InlineData("MEMBER@EXAMPLE.COM")]
    [InlineData("  member@example.com  ")]
    public void A_password_equal_to_the_email_is_refused_however_it_is_spelled(string password)
    {
        // Not a composition rule — the address is the one string an attacker already has.
        // Normalized on both sides so a different capitalization does not slip it through.
        Assert.Equal(PasswordPolicyViolation.SameAsEmail, PasswordPolicy.Check(password, Email));
    }

    [Fact]
    public void A_password_that_merely_contains_the_email_is_allowed()
    {
        // The companion to the test above, and the line the rule deliberately stops at.
        // "Contains" would be a composition rule by another name, and FR-002 forbids
        // those outright.
        Assert.Equal(
            PasswordPolicyViolation.None,
            PasswordPolicy.Check("member@example.com-and-more", Email));
    }

    [Fact]
    public void No_composition_rule_is_imposed()
    {
        // FR-002 is a prohibition, not an omission: this asserts the absence of a rule.
        // Every one of these is long enough and unlike the email, and every one passes —
        // adding "must contain a digit" or "must contain a symbol" fails this test, which
        // is exactly what should happen.
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("aaaaaaaaaa", Email));
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("correct horse battery staple", Email));
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("0000000000", Email));
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("한국어비밀번호입니다", Email));
    }

    [Fact]
    public void Surrounding_whitespace_in_a_password_is_kept_and_counts_toward_length()
    {
        // A member who typed a leading space chose it. Trimming would mean the password
        // they typed is not the password that was stored — and would silently make two
        // different passwords the same one.
        Assert.Equal(PasswordPolicyViolation.None, PasswordPolicy.Check("  12345678", Email));
        Assert.Equal(PasswordPolicyViolation.TooShort, PasswordPolicy.Check("  1234567", Email));
    }

    [Fact]
    public void Length_is_judged_before_the_email_comparison()
    {
        // An over-long input is rejected on the cheapest test, so the expensive path is
        // never reached with a body an attacker controls the size of.
        var overLong = new string('a', 300);

        Assert.Equal(PasswordPolicyViolation.TooLong, PasswordPolicy.Check(overLong, overLong));
    }

    [Fact]
    public void Null_is_rejected_rather_than_treated_as_a_violation()
    {
        Assert.Throws<ArgumentNullException>(() => PasswordPolicy.Check(null!, Email));
        Assert.Throws<ArgumentNullException>(() => PasswordPolicy.Check("1234567890", null!));
    }
}
