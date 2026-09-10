using FitForge.Domain.Members;

namespace FitForge.Domain.Tests;

/// <summary>
/// FR-001: the email is unique case-insensitively and after trimming. These tests cover
/// the code half of that rule; <c>UQ_Member_NormalizedEmail</c> covers the other half,
/// and the two are deliberately redundant.
/// </summary>
public class EmailAddressTests
{
    [Theory]
    [InlineData("member@example.com")]
    [InlineData("MEMBER@EXAMPLE.COM")]
    [InlineData("Member@Example.Com")]
    [InlineData("  member@example.com  ")]
    [InlineData("\tmember@example.com\r\n")]
    public void Every_spelling_of_one_address_normalizes_to_one_value(string written)
    {
        Assert.Equal("MEMBER@EXAMPLE.COM", EmailAddress.Normalize(written));
    }

    [Fact]
    public void Two_spellings_that_differ_only_in_case_or_whitespace_collide()
    {
        // This is FR-001 stated directly: the registration path refuses the second of
        // these as a duplicate, and it can only do so because they normalize equal.
        Assert.Equal(
            EmailAddress.Normalize(" Anas.M@Example.com "),
            EmailAddress.Normalize("anas.m@example.COM"));
    }

    [Fact]
    public void Addresses_that_genuinely_differ_do_not_collide()
    {
        // The companion to the test above. Without this one, a Normalize that returned a
        // constant would pass the whole suite.
        Assert.NotEqual(
            EmailAddress.Normalize("anas.m@example.com"),
            EmailAddress.Normalize("ahmad@example.com"));
    }

    [Fact]
    public void Normalization_is_case_insensitive_in_the_invariant_culture()
    {
        // Upper, not lower, and invariant, not current: plan D-note in EmailAddress.
        // A current-culture ToLower in a Turkish locale maps 'I' to a dotless 'ı', so
        // ADMIN@X.COM and admin@x.com would stop matching. Asserting the chosen direction
        // keeps a future "tidy-up" to ToLowerInvariant visible as a failing test.
        Assert.Equal("ADMIN@X.COM", EmailAddress.Normalize("Admin@x.com"));
    }

    [Theory]
    [InlineData("  member@example.com  ", "member@example.com")]
    [InlineData("Anas.M@Example.com", "Anas.M@Example.com")]
    public void The_display_form_trims_but_keeps_the_member_capitalization(string written, string expected)
    {
        Assert.Equal(expected, EmailAddress.ForDisplay(written));
    }

    [Fact]
    public void Null_is_rejected_rather_than_normalized_to_something()
    {
        Assert.Throws<ArgumentNullException>(() => EmailAddress.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => EmailAddress.ForDisplay(null!));
    }
}
