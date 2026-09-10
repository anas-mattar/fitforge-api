namespace FitForge.Domain.Members;

/// <summary>
/// How much training history a member brings.
/// </summary>
/// <remarks>
/// VI-021 renders these as Intermediate, Beginner, Advanced — the default first, then the
/// natural order. The numeric values below follow the natural order; the screen orders the
/// options itself, because the default belonging at the top is a presentation concern and
/// storing it as one would make <c>Beginner</c> a non-zero default for no reason.
/// </remarks>
public enum ExperienceLevel
{
    Beginner = 0,
    Intermediate = 1,
    Advanced = 2,
}
