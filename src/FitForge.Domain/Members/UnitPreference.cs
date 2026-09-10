namespace FitForge.Domain.Members;

/// <summary>
/// How measurements are shown to a member. A display setting and nothing more.
/// </summary>
/// <remarks>
/// Training invariant 4: loads are stored in kilograms and lengths in centimetres, always.
/// Changing this value MUST NOT rewrite a stored measurement — conversion happens in the
/// presentation layer, at render time (spec FR-011, VI-028).
/// </remarks>
public enum UnitPreference
{
    /// <summary>kg / cm — the canonical units, and the default.</summary>
    Metric = 0,

    /// <summary>lb / in — converted for display only.</summary>
    Imperial = 1,
}
