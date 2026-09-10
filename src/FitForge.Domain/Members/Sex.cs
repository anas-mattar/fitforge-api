namespace FitForge.Domain.Members;

/// <summary>
/// Optional, member-supplied, and never inferred.
/// </summary>
/// <remarks>
/// Training invariant 10 keeps the personal data set minimal: this exists because training
/// norms differ, it is nullable, and <c>PreferNotToSay</c> is a real answer rather than an
/// absence — a member who declines has said something, and the product should not keep
/// asking.
/// </remarks>
public enum Sex
{
    Female = 0,
    Male = 1,
    PreferNotToSay = 2,
}
