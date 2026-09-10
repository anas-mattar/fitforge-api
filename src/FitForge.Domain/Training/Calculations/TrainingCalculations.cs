namespace FitForge.Domain.Training.Calculations;

/// <summary>
/// The single module that owns every derived training number. Empty until feature 009.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/rulebooks/backend-rules.md</c> requires that set volume, session volume,
/// estimated 1RM, personal records, streak and adherence live in one named module rather
/// than being recomputed wherever they are displayed. This is that module.
/// </para>
/// <para>
/// Three rules govern everything added here, all from
/// <c>modules/training/training-invariants.md</c> and
/// <c>docs/product/fitforge-logic.md</c> §4:
/// </para>
/// <list type="number">
///   <item>
///     Every value here is <b>derived, never stored</b> (invariant 5). If a number in this
///     module ever gains a database column, the invariant is broken — the column and the
///     function will disagree the first time a set entry is corrected.
///   </item>
///   <item>
///     Compute in full precision and round <b>once</b>, at display. A total is never the
///     sum of rounded parts.
///   </item>
///   <item>
///     Every function here is pure and takes its inputs as arguments. Nothing in this
///     namespace may reach for a data source — which is why FitForge.Domain references no
///     package (ADR-001 §4.2).
///   </item>
/// </list>
/// <para>
/// The class is <c>partial</c> so later features add their own file per calculation
/// rather than growing one unreviewable file.
/// </para>
/// </remarks>
public static partial class TrainingCalculations
{
}
