using GroupWeaver.Core.Model;

namespace GroupWeaver.App.ViewModels;

/// <summary>
/// One per-kind tally row of the ADR-022 D5 scope-summary card (shown when nothing is
/// selected, replacing the centered "Click a node…" void): the object <see cref="Kind"/>
/// and how many drawn nodes carry it. The card reuses the AP 2.2 <c>AdObjectKind</c>
/// badge palette (<see cref="Views.AdObjectKindConverters"/>) for the kind swatch, so the
/// summary reads in the same color vocabulary as the graph and the root picker.
/// </summary>
public sealed record KindTallyRow(AdObjectKind Kind, int Count);
