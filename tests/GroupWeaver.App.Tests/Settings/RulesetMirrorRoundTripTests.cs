using System.Linq;

using GroupWeaver.App.Settings;
using GroupWeaver.Core.Model;
using GroupWeaver.Core.Rules;

using Xunit;

namespace GroupWeaver.App.Tests.Settings;

/// <summary>
/// Pins the AP 3.3 / S2 editable-mirror SAFETY contract (ADR-011 §2, the spec's
/// "single most important pin"): the immutable <see cref="Ruleset"/> records
/// (<c>required</c>/<c>init</c>, un-bindable) are mirrored into an editable
/// <see cref="SettingsViewModel"/> tree via <see cref="SettingsViewModel.LoadFrom"/>,
/// edited, then projected back to an immutable <see cref="Ruleset"/> via
/// <see cref="SettingsViewModel.BuildRuleset"/>. The single save/import/apply
/// validation gate is <see cref="RulesetLoader.Load"/> (never a parallel
/// validator) — these tests assert the mirror is a faithful identity over the
/// model so a round-trip cannot silently corrupt a user's ruleset.
///
/// <para><b>The byte fixed point (highest risk, ADR-011 open-risk #1).</b>
/// <c>Serialize(BuildRuleset(LoadFrom(LoadDefault())))</c> must be BYTE-EQUAL to
/// <c>Serialize(LoadDefault())</c>. This proves two things at once that no looser
/// property-equality check can: (1) the sparse nesting matrix survives — every
/// source cell keeps its PRESENCE (<c>NestingCellEditor.Present</c>) so the
/// serializer emits exactly the source keys, never a dense-widened
/// <c>$.nesting.matrix</c> (which would break <c>Serialize(Load(x))==x</c> and the
/// <c>examples/rulesets/</c> drift pin); and (2) the
/// <c>deny</c>(<c>false,null</c>) vs <c>error</c>/<c>warning</c>/<c>info</c>
/// (<c>false,Severity</c>) token distinction survives — the DL row's
/// <c>"External": "info"</c> and the UG row's <c>"UniversalGroup": "warning"</c>
/// in the default ruleset are <c>NestingCell(false, Info)</c> /
/// <c>NestingCell(false, Warning)</c> and must NOT collapse to <c>"deny"</c>.</para>
///
/// <para>Also pinned: dn/name XOR survives a <see cref="MatchEntryEditor"/>
/// round-trip; the <see cref="MatchEndpoint"/> is preserved only on nesting
/// exceptions (forced to <see cref="MatchEndpoint.Any"/> — which serializes to
/// null, never <c>"any"</c> — everywhere else); the circular/empty-group
/// <see cref="SimpleRule.RuleId"/> is set from <see cref="RuleIds"/> and never
/// serialized; <see cref="Ruleset.SchemaVersion"/> stays 1; and editing one
/// matrix cell's <see cref="CellChoice"/> to <see cref="CellChoice.Error"/> emits
/// exactly that one cell as <c>"error"</c> while leaving every other cell's
/// presence unchanged.</para>
///
/// RED until the mirror tree exists under <c>src/App/Settings/</c>
/// (<c>SettingsViewModel</c> with <c>LoadFrom</c>/<c>BuildRuleset</c>,
/// <c>NestingCellEditor</c>+<c>CellChoice</c>+<c>Present</c>, <c>MatchEntryEditor</c>
/// +<c>EntryMode</c>+<c>EndpointEditable</c>, <c>NestingEditor</c>,
/// <c>NamingRuleEditor</c>, <c>SimpleRuleEditor</c>, <c>MetadataEditor</c>).
/// Pure VM logic — no headless UI, no dispatcher.
/// </summary>
public sealed class RulesetMirrorRoundTripTests
{
    // === THE byte fixed point ===================================================

    /// <summary>
    /// The single most important pin of AP 3.3: passing the embedded default
    /// through the editable mirror unchanged must be a byte-for-byte identity on
    /// the serialized form. Any sparse-matrix widening or any deny/error token
    /// collapse breaks this immediately.
    /// </summary>
    [Fact]
    public void Default_ThroughMirror_IsByteEqualOnSerialize_TheFixedPoint()
    {
        var original = RulesetLoader.LoadDefault();

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        Assert.Equal(
            RulesetSerializer.Serialize(original),
            RulesetSerializer.Serialize(rebuilt));
    }

    /// <summary>
    /// And the rebuilt ruleset must itself be loadable (it passed through the
    /// same shape the save gate re-parses) — the mirror never produces a ruleset
    /// the loader would reject.
    /// </summary>
    [Fact]
    public void Default_ThroughMirror_RebuiltRuleset_StillLoads()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        var reloaded = RulesetLoader.Load(RulesetSerializer.Serialize(rebuilt));

        Assert.True(reloaded.Success);
    }

    // === sparse-matrix PRESENCE: deny vs error/warning/info token survival ======

    /// <summary>
    /// The DL row's <c>External: info</c> and the UG row's <c>UniversalGroup:
    /// warning</c> are deny cells WITH a severity override — they must round-trip
    /// as <c>info</c>/<c>warning</c>, never as a bare <c>deny</c>. A mirror that
    /// modeled a cell as only a bool would lose this distinction silently, and
    /// the byte fixed point would catch it — but pin the semantic directly too.
    /// </summary>
    [Fact]
    public void DenyWithOverride_Cells_KeepTheirSeverityToken_NotCollapsedToDeny()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        var dlExternal = rebuilt.Nesting.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.External);
        Assert.False(dlExternal.Allowed);
        Assert.Equal(RuleSeverity.Info, dlExternal.SeverityOverride);

        var ugUniversal = rebuilt.Nesting.Cell(AdObjectKind.UniversalGroup, AdObjectKind.UniversalGroup);
        Assert.False(ugUniversal.Allowed);
        Assert.Equal(RuleSeverity.Warning, ugUniversal.SeverityOverride);
    }

    /// <summary>
    /// A plain <c>deny</c> cell (DL←User in the default) stays
    /// <c>NestingCell(false, null)</c> — it must NOT pick up the rule severity as
    /// a phantom override (which would serialize as <c>"error"</c> and break the
    /// fixed point).
    /// </summary>
    [Fact]
    public void PlainDeny_Cell_StaysFalseNull_NoPhantomOverride()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        var dlUser = rebuilt.Nesting.Cell(AdObjectKind.DomainLocalGroup, AdObjectKind.User);
        Assert.False(dlUser.Allowed);
        Assert.Null(dlUser.SeverityOverride);
    }

    /// <summary>
    /// An <c>allow</c> cell stays <c>NestingCell(true, null)</c>.
    /// </summary>
    [Fact]
    public void Allow_Cell_StaysTrueNull()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        var ggUser = rebuilt.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.User);
        Assert.True(ggUser.Allowed);
        Assert.Null(ggUser.SeverityOverride);
    }

    // === editing one matrix cell ================================================

    /// <summary>
    /// Editing the GG←GG cell (default <c>allow</c>) to
    /// <see cref="CellChoice.Error"/> and rebuilding must serialize EXACTLY that
    /// one cell as <c>"error"</c> and change nothing else — the surgical-edit
    /// guarantee the examples/rulesets sample (S9: a GG←GG error cell) depends on.
    /// </summary>
    [Fact]
    public void EditingOneCellToError_EmitsExactlyThatCellAsError_OthersUnchanged()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        FindCell(vm, AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;
        var rebuilt = vm.BuildRuleset();

        // Exactly that cell flipped.
        var ggGg = rebuilt.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup);
        Assert.False(ggGg.Allowed);
        Assert.Equal(RuleSeverity.Error, ggGg.SeverityOverride);

        // Every other cell of the default is byte-identical: the ONLY serialized
        // difference between the rebuilt and the default is the GG.GlobalGroup
        // token "allow" -> "error". The GlobalGroup-member "allow" token is NOT
        // unique in the default (the GG/DL/UG rows each allow a GlobalGroup
        // member), but the GG row serializes first, so the edited cell is the
        // FIRST occurrence — replacing exactly it (and asserting the DL/UG
        // GlobalGroup cells stay "allow") is the surgical guarantee.
        var defaultJson = RulesetSerializer.Serialize(RulesetLoader.LoadDefault());
        var editedJson = RulesetSerializer.Serialize(rebuilt);

        Assert.NotEqual(defaultJson, editedJson);

        int firstAllow = defaultJson.IndexOf("\"GlobalGroup\": \"allow\"", System.StringComparison.Ordinal);
        var expected = string.Concat(
            defaultJson.AsSpan(0, firstAllow),
            "\"GlobalGroup\": \"error\"",
            defaultJson.AsSpan(firstAllow + "\"GlobalGroup\": \"allow\"".Length));
        Assert.Equal(expected, editedJson);
    }

    /// <summary>
    /// Editing one cell must not silently widen the matrix: the rebuilt matrix has
    /// exactly the same row keys and the same per-row column keys as the default
    /// (presence preserved, no dense fill).
    /// </summary>
    [Fact]
    public void EditingOneCell_DoesNotWidenTheMatrix_PresenceUnchanged()
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());
        FindCell(vm, AdObjectKind.GlobalGroup, AdObjectKind.GlobalGroup).Choice = CellChoice.Error;

        var rebuilt = vm.BuildRuleset();
        var original = RulesetLoader.LoadDefault();

        Assert.Equal(
            original.Nesting.Matrix.Keys.OrderBy(k => k),
            rebuilt.Nesting.Matrix.Keys.OrderBy(k => k));

        foreach (var parent in original.Nesting.Matrix.Keys)
        {
            Assert.Equal(
                original.Nesting.Matrix[parent].Keys.OrderBy(k => k),
                rebuilt.Nesting.Matrix[parent].Keys.OrderBy(k => k));
        }
    }

    // === every CellChoice maps to the exact inverse of CellToken =================

    /// <summary>
    /// The <see cref="CellChoice"/> → <see cref="NestingCell"/> mapping is the
    /// exact inverse of <c>RulesetSerializer.CellToken</c> (ADR-011 §2 "the
    /// round-trip crux"): Allow→(true,null), Deny→(false,null),
    /// Error→(false,Error), Warning→(false,Warning), Info→(false,Info). Drive each
    /// choice through a single cell and assert the rebuilt cell + its serialized
    /// token.
    /// </summary>
    [Theory]
    [InlineData(CellChoice.Allow, true, null, "allow")]
    [InlineData(CellChoice.Deny, false, null, "deny")]
    [InlineData(CellChoice.Error, false, RuleSeverity.Error, "error")]
    [InlineData(CellChoice.Warning, false, RuleSeverity.Warning, "warning")]
    [InlineData(CellChoice.Info, false, RuleSeverity.Info, "info")]
    public void CellChoice_MapsToExactNestingCell_AndToken(
        CellChoice choice, bool allowed, RuleSeverity? severityOverride, string token)
    {
        var vm = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault());

        // Drive the GG row's User cell (default "allow") to the choice under test.
        FindCell(vm, AdObjectKind.GlobalGroup, AdObjectKind.User).Choice = choice;
        var rebuilt = vm.BuildRuleset();

        var cell = rebuilt.Nesting.Cell(AdObjectKind.GlobalGroup, AdObjectKind.User);
        Assert.Equal(allowed, cell.Allowed);
        Assert.Equal(severityOverride, cell.SeverityOverride);

        // And the serialized token is exactly the loader's inverse vocabulary.
        var json = RulesetSerializer.Serialize(rebuilt);
        Assert.Contains($"\"User\": \"{token}\"", json);
    }

    // === metadata + schema version ==============================================

    /// <summary>
    /// Name/Description/Author survive the round-trip verbatim; SchemaVersion is
    /// always 1 regardless of any edit (it is never a user-editable field).
    /// </summary>
    [Fact]
    public void Metadata_AndSchemaVersion_SurviveRoundTrip()
    {
        var original = RulesetLoader.LoadDefault();

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        Assert.Equal(original.Name, rebuilt.Name);
        Assert.Equal(original.Description, rebuilt.Description);
        Assert.Equal(original.Author, rebuilt.Author);
        Assert.Equal(1, rebuilt.SchemaVersion);
    }

    // === circular / empty-group RuleId from RuleIds, never serialized ===========

    /// <summary>
    /// The fixed <see cref="SimpleRule.RuleId"/> is reconstructed from
    /// <see cref="RuleIds.Circular"/>/<see cref="RuleIds.EmptyGroup"/> by the
    /// mirror (it is never user-edited) and is NEVER serialized — the serializer
    /// derives circular/empty position from the schema, so the rebuilt ids must be
    /// exactly the canonical ones for the byte fixed point to hold.
    /// </summary>
    [Fact]
    public void SimpleRule_RuleIds_AreSetFromRuleIds()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        Assert.Equal(RuleIds.Circular, rebuilt.Circular.RuleId);
        Assert.Equal(RuleIds.EmptyGroup, rebuilt.EmptyGroup.RuleId);
    }

    /// <summary>
    /// A RuleId never reaches the serialized form — confirm neither id token nor
    /// a literal "ruleId" property appears in the bytes (it is structural, not
    /// data). The default's <c>circular</c>/<c>emptyGroup</c> sections carry only
    /// enabled/severity/exceptions.
    /// </summary>
    [Fact]
    public void SimpleRule_RuleId_IsNeverSerialized()
    {
        var rebuilt = SettingsViewModel.LoadFrom(RulesetLoader.LoadDefault()).BuildRuleset();

        var json = RulesetSerializer.Serialize(rebuilt);

        Assert.DoesNotContain("ruleId", json, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empty-group", json, System.StringComparison.Ordinal);
    }

    // === MatchEntry: dn/name XOR survives =======================================

    /// <summary>
    /// Every default ignore entry is a dn entry; after a round-trip each must
    /// still carry its Dn set and its Name null — the XOR invariant the loader
    /// enforces is preserved by the mirror, not flattened into a single field that
    /// loses which side was set.
    /// </summary>
    [Fact]
    public void IgnoreEntries_DnXorName_Survives_DnSide()
    {
        var original = RulesetLoader.LoadDefault();

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        Assert.Equal(original.Ignore.Count, rebuilt.Ignore.Count);
        foreach (var entry in rebuilt.Ignore)
        {
            Assert.NotNull(entry.Dn);
            Assert.Null(entry.Name);
        }
    }

    /// <summary>
    /// A name entry survives as a name entry (Dn null). Built directly because the
    /// default ignore list is all-dn; the mirror's <see cref="EntryMode"/> toggle
    /// must round-trip a name entry without leaking it onto the Dn side.
    /// </summary>
    [Fact]
    public void IgnoreEntry_NameSide_Survives_DnStaysNull()
    {
        var original = RulesetLoader.LoadDefault() with
        {
            Ignore = new[]
            {
                new MatchEntry { Name = "GG_Legacy_*", Note = "name entry, not a dn" },
            },
        };

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        var entry = Assert.Single(rebuilt.Ignore);
        Assert.Equal("GG_Legacy_*", entry.Name);
        Assert.Null(entry.Dn);
        Assert.Equal("name entry, not a dn", entry.Note);
    }

    /// <summary>
    /// The free-form <see cref="MatchEntry.Note"/> survives verbatim, including a
    /// control character (#45: notes are data, round-trip untouched; the plain-text
    /// RENDER rule is a separate S7 pin, but the value must not be mangled here).
    /// </summary>
    [Fact]
    public void MatchEntry_Note_SurvivesVerbatim_IncludingControlChar()
    {
        const string note = "firstline[31m";
        var original = RulesetLoader.LoadDefault() with
        {
            Ignore = new[] { new MatchEntry { Dn = "CN=x,*", Note = note } },
        };

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        Assert.Equal(note, Assert.Single(rebuilt.Ignore).Note);
    }

    // === endpoint preserved ONLY in nesting exceptions ==========================

    /// <summary>
    /// A nesting exception is the only place an endpoint may be non-Any
    /// (<c>parent</c>/<c>member</c>) — it must survive the round-trip. The mirror's
    /// <see cref="MatchEntryEditor.EndpointEditable"/> is true for nesting
    /// exceptions, so the chosen endpoint is preserved into the rebuilt ruleset.
    /// </summary>
    [Theory]
    [InlineData(MatchEndpoint.Parent)]
    [InlineData(MatchEndpoint.Member)]
    [InlineData(MatchEndpoint.Any)]
    public void NestingException_Endpoint_SurvivesRoundTrip(MatchEndpoint endpoint)
    {
        var original = WithNestingException(
            new MatchEntry { Dn = "CN=svc-*,*", Note = "scoped exception", Endpoint = endpoint });

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        var exception = Assert.Single(rebuilt.Nesting.Exceptions);
        Assert.Equal(endpoint, exception.Endpoint);
        Assert.Equal("CN=svc-*,*", exception.Dn);
    }

    /// <summary>
    /// Naming/circular/empty-group exceptions are NOT endpoint-bearing. Even if a
    /// source somehow carried a non-Any endpoint there, the mirror forces it to
    /// <see cref="MatchEndpoint.Any"/> on build (EndpointEditable=false) — which
    /// serializes to null, never <c>"any"</c>. Drive each non-nesting exception
    /// list and assert the endpoint comes back Any.
    /// </summary>
    [Fact]
    public void NonNestingException_Endpoint_ForcedToAny_OnBuild()
    {
        // A naming-rule exception whose source endpoint is (illegally) Parent.
        var original = RulesetLoader.LoadDefault();
        var namingWithException = original.Naming[0] with
        {
            Exceptions = new[]
            {
                new MatchEntry { Name = "GG_Legacy*", Endpoint = MatchEndpoint.Parent },
            },
        };
        var withNamingException = original with
        {
            Naming = new[] { namingWithException }.Concat(original.Naming.Skip(1)).ToList(),
            Circular = original.Circular with
            {
                Exceptions = new[] { new MatchEntry { Dn = "CN=loop,*", Endpoint = MatchEndpoint.Member } },
            },
            EmptyGroup = original.EmptyGroup with
            {
                Exceptions = new[] { new MatchEntry { Dn = "CN=shell,*", Endpoint = MatchEndpoint.Parent } },
            },
        };

        var rebuilt = SettingsViewModel.LoadFrom(withNamingException).BuildRuleset();

        Assert.Equal(MatchEndpoint.Any, Assert.Single(rebuilt.Naming[0].Exceptions).Endpoint);
        Assert.Equal(MatchEndpoint.Any, Assert.Single(rebuilt.Circular.Exceptions).Endpoint);
        Assert.Equal(MatchEndpoint.Any, Assert.Single(rebuilt.EmptyGroup.Exceptions).Endpoint);
    }

    /// <summary>
    /// And the forced-Any endpoint never serializes as the string <c>"any"</c>
    /// (it is omitted as null) — guards the serializer's
    /// <c>EndpointToken(Any)=null</c> contract through the mirror, so a non-nesting
    /// exception adds no spurious <c>endpoint</c> key.
    /// </summary>
    [Fact]
    public void NonNestingException_ForcedAnyEndpoint_IsOmitted_NotWrittenAsAny()
    {
        var original = RulesetLoader.LoadDefault();
        var withCircularException = original with
        {
            Circular = original.Circular with
            {
                Exceptions = new[] { new MatchEntry { Dn = "CN=loop,*" } },
            },
        };

        var json = RulesetSerializer.Serialize(
            SettingsViewModel.LoadFrom(withCircularException).BuildRuleset());

        Assert.DoesNotContain("\"any\"", json, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"endpoint\"", json, System.StringComparison.Ordinal);
    }

    // === naming rules: file order + fields survive ==============================

    /// <summary>
    /// Naming rules round-trip in file order with every field intact (id, enabled,
    /// severity, kind, pattern, description). A flat list keyed on id — multiple
    /// rules per kind are legal, so order and id identity must both survive.
    /// </summary>
    [Fact]
    public void NamingRules_SurviveInFileOrder_WithAllFields()
    {
        var original = RulesetLoader.LoadDefault();

        var rebuilt = SettingsViewModel.LoadFrom(original).BuildRuleset();

        Assert.Equal(
            original.Naming.Select(r => r.Id),
            rebuilt.Naming.Select(r => r.Id));

        for (var i = 0; i < original.Naming.Count; i++)
        {
            Assert.Equal(original.Naming[i].Enabled, rebuilt.Naming[i].Enabled);
            Assert.Equal(original.Naming[i].Severity, rebuilt.Naming[i].Severity);
            Assert.Equal(original.Naming[i].Kind, rebuilt.Naming[i].Kind);
            Assert.Equal(original.Naming[i].Pattern, rebuilt.Naming[i].Pattern);
            Assert.Equal(original.Naming[i].Description, rebuilt.Naming[i].Description);
        }
    }

    // === helpers ================================================================

    /// <summary>The mirror's nesting cell editor for a parent←member pairing.
    /// Pins that the matrix grid exposes editable cells addressable by kind.</summary>
    private static NestingCellEditor FindCell(
        SettingsViewModel vm, AdObjectKind parent, AdObjectKind member) =>
        vm.Nesting.Cell(parent, member);

    private static Ruleset WithNestingException(MatchEntry exception)
    {
        var original = RulesetLoader.LoadDefault();
        return original with
        {
            Nesting = original.Nesting with { Exceptions = new[] { exception } },
        };
    }
}
