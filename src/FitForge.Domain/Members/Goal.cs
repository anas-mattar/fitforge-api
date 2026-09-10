namespace FitForge.Domain.Members;

/// <summary>
/// What a member is training for.
/// </summary>
/// <remarks>
/// Member order is not arbitrary: VI-020 fixes the order the profile screen's select
/// renders, and the screen reads it from here. Reordering these members changes the UI.
/// </remarks>
public enum Goal
{
    Hypertrophy = 0,
    Strength = 1,
    Endurance = 2,
    GeneralFitness = 3,
}
