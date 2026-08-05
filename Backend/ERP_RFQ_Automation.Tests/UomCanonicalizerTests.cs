using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Uom;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The canonicaliser decides what "the same unit" means, and a wrong answer here is a wrong
/// QUANTITY on a quote to a customer. Every case below is written against the real production
/// corpus: the 17 distinct UnitOfMeasure spellings across 2,966 stored lead line items —
/// each 2868, Pack 25, EA 19, blank 12, NOS 12, Kit 9, Package 4, Activ.unit 4, pcs 3, Set 3,
/// Pallet 1, piece 1, length 1, Pcs 1, ST 1, FT 1, Pipe 1.
/// </summary>
public sealed class UomCanonicalizerTests
{
    // ── The count family: 2,904 of the 2,966 rows, spelled six ways ──────────────────────

    [Theory]
    [InlineData("each")]      // 2,868 rows
    [InlineData("EA")]        //    19 rows
    [InlineData("NOS")]       //    12 rows
    [InlineData("pcs")]       //     3 rows
    [InlineData("Pcs")]       //     1 row
    [InlineData("piece")]     //     1 row
    [InlineData("EACH")]      // minted by the conversion path's deleted ToUpperInvariant()
    [InlineData("ea.")]
    [InlineData("PC")]
    [InlineData("Nos.")]
    [InlineData("  pieces  ")]
    public void Every_production_spelling_of_a_count_folds_to_one_code(string raw)
    {
        var result = UomCanonicalizer.Canonicalize(raw);
        Assert.Equal(UomResolution.Canonical, result.Resolution);
        Assert.Equal("EA", result.Value);
        Assert.Equal("EA", result.CanonicalCode);
    }

    [Theory]
    [InlineData("Set")]   // 3 rows
    [InlineData("Kit")]   // 9 rows — a kit is priced as one assembly, exactly like a set
    [InlineData("SETS")]
    public void Sets_and_kits_fold_to_one_code(string raw)
        => Assert.Equal("SET", UomCanonicalizer.Canonicalize(raw).Value);

    [Fact]
    public void The_sap_activity_unit_is_its_own_code_not_a_physical_each()
    {
        // "Activ.unit" (4 rows) is SAP's activity unit — a unit of WORK. It counts like an
        // each, but folding it into EA would erase that these lines are service lines.
        var result = UomCanonicalizer.Canonicalize("Activ.unit");
        Assert.Equal(UomResolution.Canonical, result.Resolution);
        Assert.Equal("AU", result.Value);
        Assert.NotEqual("EA", result.Value);
        Assert.Equal("AU", UomCanonicalizer.Canonicalize("ACTIVITY UNIT").Value);
    }

    // ── The refusals: the half of this class that exists to prevent wrong quantities ─────

    [Theory]
    [InlineData("Pack")]      // 25 rows
    [InlineData("Package")]   //  4 rows
    [InlineData("Pallet")]    //  1 row
    [InlineData("Box")]
    [InlineData("Carton")]
    [InlineData("Drum")]
    [InlineData("Bundle")]
    public void Packaging_is_never_collapsed_into_a_count(string raw)
    {
        // A pallet of gaskets and a pallet of cable are different counts, and the string says
        // which for neither. Quoting "1 Pallet" as "1 each" is the exact failure this class
        // exists to prevent, so the wording survives untouched and a human is asked.
        var result = UomCanonicalizer.Canonicalize(raw);
        Assert.Equal(UomResolution.NeedsReview, result.Resolution);
        Assert.Equal(UomReviewReason.Packaging, result.ReviewReason);
        Assert.Equal(raw, result.Value);
        Assert.Null(result.CanonicalCode);
        Assert.NotEqual("EA", result.Value);
    }

    [Theory]
    [InlineData("length")]   // 1 row
    [InlineData("Pipe")]     // 1 row
    [InlineData("Coil")]
    [InlineData("Roll")]
    public void Form_factors_are_never_collapsed_into_a_count_or_a_length(string raw)
    {
        // "3 lengths" is a number only once someone states how long a length is. It is
        // neither 3 pieces nor 3 metres.
        var result = UomCanonicalizer.Canonicalize(raw);
        Assert.Equal(UomResolution.NeedsReview, result.Resolution);
        Assert.Equal(UomReviewReason.FormFactor, result.ReviewReason);
        Assert.Equal(raw, result.Value);
    }

    [Theory]
    [InlineData("ST")]    // 1 row: Stück (piece) in SAP, "set" elsewhere, short ton in US trade
    [InlineData("ton")]   // metric / short / long differ by up to 12%
    [InlineData("gal")]   // US vs imperial
    public void Genuinely_ambiguous_tokens_are_refused_rather_than_guessed(string raw)
    {
        var result = UomCanonicalizer.Canonicalize(raw);
        Assert.Equal(UomResolution.NeedsReview, result.Resolution);
        Assert.Equal(UomReviewReason.Ambiguous, result.ReviewReason);
        Assert.Null(result.CanonicalCode);
    }

    [Fact]
    public void Dimensional_units_resolve_as_themselves_and_never_as_a_count()
    {
        // FT (1 row) is a perfectly resolvable unit — as FT. What it must never become is EA.
        var feet = UomCanonicalizer.Canonicalize("FT");
        Assert.Equal(UomResolution.Canonical, feet.Resolution);
        Assert.Equal("FT", feet.Value);

        Assert.Equal("M", UomCanonicalizer.Canonicalize("metres").Value);
        Assert.Equal("M", UomCanonicalizer.Canonicalize("Linear Meter").Value);
        Assert.Equal("M2", UomCanonicalizer.Canonicalize("sq.m").Value);
        Assert.Equal("M2", UomCanonicalizer.Canonicalize("m²").Value);   // NFKD folds the superscript
        Assert.Equal("M3", UomCanonicalizer.Canonicalize("CBM").Value);
        Assert.Equal("KG", UomCanonicalizer.Canonicalize("Kgs").Value);
        Assert.Equal("HR", UomCanonicalizer.Canonicalize("man-hours").Value);
    }

    [Fact]
    public void Fixed_count_groupings_keep_their_own_code_because_quantity_is_never_rescaled()
    {
        // A dozen is twelve. Nothing here multiplies a quantity, so "5 DZ" must not become
        // "5 EA" — it keeps a code of its own and the 12 stays implicit, as the document had it.
        Assert.Equal("DZ", UomCanonicalizer.Canonicalize("dozen").Value);
        Assert.Equal("PR", UomCanonicalizer.Canonicalize("Pairs").Value);
    }

    // ── Absence, junk and idempotency ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_unit_stays_missing_and_is_never_defaulted(string? raw)
    {
        // 12 production rows are blank. Defaulting them to EA would manufacture agreement
        // with a document that stated no unit at all.
        var result = UomCanonicalizer.Canonicalize(raw);
        Assert.Equal(UomResolution.Absent, result.Resolution);
        Assert.Null(result.Value);
        Assert.Null(UomCanonicalizer.EquivalenceKey(raw));
    }

    [Fact]
    public void A_quantity_that_leaked_into_the_unit_column_is_stripped_but_junk_is_kept()
    {
        Assert.Equal("EA", UomCanonicalizer.Canonicalize("10 EA").Value);
        Assert.Equal("EA", UomCanonicalizer.Canonicalize("1,000 pcs").Value);

        // "10" alone carries no unit — but it is NOT absence. Nulling it would read as "the
        // document stated no unit"; the reviewer needs to see that the extractor put a number
        // in the unit column.
        var junk = UomCanonicalizer.Canonicalize("10");
        Assert.Equal(UomResolution.NeedsReview, junk.Resolution);
        Assert.Equal("10", junk.Value);
    }

    [Fact]
    public void Canonicalisation_is_idempotent_so_replaying_a_row_never_changes_it()
    {
        foreach (var raw in new[] { "each", "Pack", "Activ.unit", "FT", "ST", "10", null })
        {
            var once = UomCanonicalizer.Canonicalize(raw).Value;
            Assert.Equal(once, UomCanonicalizer.Canonicalize(once).Value);
        }
    }

    // ── Equivalence key: what duplicate detection hashes ─────────────────────────────────

    [Fact]
    public void One_unit_spelled_five_ways_produces_one_equivalence_key()
    {
        // This is why duplicate RFQs failed to dedup: the fingerprint hashed the spelling.
        var keys = new[] { "each", "EA", "pcs", "Pcs", "piece", "NOS", "EACH" }
            .Select(UomCanonicalizer.EquivalenceKey).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal("EA", keys[0]);
    }

    [Fact]
    public void Two_units_we_refuse_to_equate_keep_different_equivalence_keys()
    {
        Assert.NotEqual(UomCanonicalizer.EquivalenceKey("Pack"), UomCanonicalizer.EquivalenceKey("each"));
        Assert.NotEqual(UomCanonicalizer.EquivalenceKey("Pallet"), UomCanonicalizer.EquivalenceKey("Pack"));
        Assert.NotEqual(UomCanonicalizer.EquivalenceKey("FT"), UomCanonicalizer.EquivalenceKey("each"));
        // …but casing and punctuation of the SAME refused token still collapse.
        Assert.Equal(UomCanonicalizer.EquivalenceKey("Pack"), UomCanonicalizer.EquivalenceKey("  pack. "));
    }

    // ── Tenant UoM master data ───────────────────────────────────────────────────────────

    private static SetUom Row(int id, string code, string name) => new()
    {
        UomId = id, UomCode = code, UomName = name, BusinessUnitId = 1, CreatedBy = "test", IsActive = true
    };

    [Fact]
    public void The_tenant_table_supplies_the_foreign_key_but_never_the_stored_spelling()
    {
        // The tenant spells the count unit "NOS". The stored value stays the canonical EA so
        // that lead, RFQ and fingerprint agree regardless of which tenant this is; the tenant
        // row is still found, so the foreign key resolves.
        var vocabulary = SetUomVocabulary.From(new[] { Row(7, "NOS", "Numbers") });
        var result = UomCanonicalizer.Canonicalize("each", vocabulary);
        Assert.Equal("EA", result.Value);
        Assert.Equal(7, result.TenantUomId);
    }

    [Fact]
    public void A_unit_only_the_tenant_knows_is_adopted_from_their_master_data()
    {
        var vocabulary = SetUomVocabulary.From(new[] { Row(9, "BBL", "Barrel") });
        var result = UomCanonicalizer.Canonicalize("barrel", vocabulary);
        Assert.Equal(UomResolution.Canonical, result.Resolution);
        Assert.Equal("BBL", result.Value);
        Assert.Equal(9, result.TenantUomId);

        // Without that master-data row we do not invent a mapping.
        var unknown = UomCanonicalizer.Canonicalize("barrel");
        Assert.Equal(UomResolution.NeedsReview, unknown.Resolution);
        Assert.Equal(UomReviewReason.Unknown, unknown.ReviewReason);
        Assert.Equal("barrel", unknown.Value);
    }

    [Fact]
    public void Tenant_master_data_cannot_overturn_a_packaging_refusal()
    {
        // That the tenant stocks pallets still does not say how many items are on one.
        var vocabulary = SetUomVocabulary.From(new[] { Row(4, "PALLET", "Pallet") });
        var result = UomCanonicalizer.Canonicalize("Pallet", vocabulary);
        Assert.Equal(UomResolution.NeedsReview, result.Resolution);
        Assert.Equal(UomReviewReason.Packaging, result.ReviewReason);
        Assert.Equal(4, result.TenantUomId);   // the foreign key still resolves
    }

    // ── The single ingestion assignment ──────────────────────────────────────────────────

    [Fact]
    public void The_shared_ingestion_mapper_canonicalises_every_door()
    {
        // Four doors used to hold four copies of this mapping; only one shared assignment
        // remains, so this covers 100% of ingested rows rather than 25%.
        Assert.Equal("EA", Map("each").UnitOfMeasure);
        Assert.Equal("EA", Map("NOS").UnitOfMeasure);
        Assert.Equal("Pallet", Map("Pallet").UnitOfMeasure);
        Assert.Null(Map(null).UnitOfMeasure);
        Assert.Null(Map("   ").UnitOfMeasure);
    }

    private static LeadItem Map(string? unitOfMeasure)
        => LeadItemMapper.Map(Ext.Item(0.9) with { UnitOfMeasure = unitOfMeasure }, _ => null);
}
