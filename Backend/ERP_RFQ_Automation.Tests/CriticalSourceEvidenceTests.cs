using ERP_RFQ_Automation.CommercialCases.Participation;

namespace ERP_RFQ_Automation.Tests;

public sealed class CriticalSourceEvidenceTests
{
    [Fact]
    public void Exact_verified_prose_span_is_a_citation_not_typed_commercial_evidence()
    {
        var assessment = Assess(
            "Line 1: Control valve, manufacturer part number VALVE-A, quantity 2 EA",
            "VALVE-A", 2m, "EA");

        Assert.False(assessment.Complete);
        Assert.Equal(3, assessment.Missing().Count);
    }

    [Theory]
    [InlineData("Line 1: VALVE-A, quantity 3 EA", 2, "EA")]
    [InlineData("Line 1: VALVE-A, quantity 2 KG", 2, "EA")]
    [InlineData("Line 1: VALVE-A1, quantity 2 EA", 2, "EA")]
    public void Prose_span_never_proves_a_near_match(string span, int quantity, string uom)
    {
        var assessment = Assess(span, "VALVE-A", quantity, uom);

        Assert.False(assessment.Complete);
    }

    [Fact]
    public void Generic_requested_line_without_typed_quantity_and_unit_is_not_complete()
    {
        var assessment = CriticalSourceEvidence.Assess(
            [new("requestedLine", "VALVE-A", "VALVE-A")],
            [new("ProductShortName", "VALVE-A")], 2m, "EA");

        Assert.True(assessment.Identity);
        Assert.False(assessment.Quantity);
        Assert.False(assessment.UnitOfMeasure);
    }

    [Fact]
    public void Typed_exact_fields_remain_admissible()
    {
        var assessment = CriticalSourceEvidence.Assess(
            [
                new("ManufacturerPartNumber", "VALVE-A", "VALVE-A"),
                new("Quantity", "2", "2"),
                new("UnitOfMeasure", "each", "EA")
            ],
            [new("ManufacturerPartNumber", "VALVE-A")], 2m, "EA");

        Assert.True(assessment.Complete);
    }

    [Fact]
    public void Verified_span_derivation_preserves_exact_source_tokens()
    {
        var derived = CriticalSourceEvidence.DeriveFromVerifiedSpan(
            "Line 3: belt BELT-FG-1275, quantity 12.75 kg",
            [new("ManufacturerPartNumber", "BELT-FG-1275")],
            12.75m,
            "KG");

        Assert.NotNull(derived);
        Assert.Equal("ManufacturerPartNumber", derived!.IdentityFieldName);
        Assert.Equal("BELT-FG-1275", derived.IdentityRawValue);
        Assert.Equal("12.75", derived.QuantityRawValue);
        Assert.Equal("kg", derived.UnitOfMeasureRawValue);
    }

    [Theory]
    [InlineData("VALVE-A-1")]
    [InlineData("VALVE-A_1")]
    [InlineData("VALVE-A.1")]
    [InlineData("VALVE-A/1")]
    public void Strong_identifier_suffixes_never_project_as_the_requested_part(string stated)
    {
        var derived = CriticalSourceEvidence.DeriveFromVerifiedSpan(
            $"Line 1: {stated}, quantity 2 EA",
            [new("ManufacturerPartNumber", "VALVE-A")], 2m, "EA");

        Assert.Null(derived);
    }

    [Fact]
    public void Generic_description_cannot_replace_an_available_strong_identifier()
    {
        var derived = CriticalSourceEvidence.DeriveFromVerifiedSpan(
            "Control valve, manufacturer part VALVE-B, quantity 2 EA",
            [
                new("ManufacturerPartNumber", "VALVE-A"),
                new("ProductShortName", "Control valve")
            ], 2m, "EA");

        Assert.Null(derived);
    }

    [Fact]
    public void Multiple_quantity_pairs_make_a_span_ineligible_for_projection()
    {
        var derived = CriticalSourceEvidence.DeriveFromVerifiedSpan(
            "VALVE-A kit includes 2 EA seals; quote quantity 1 EA valve",
            [new("ManufacturerPartNumber", "VALVE-A")], 2m, "EA");

        Assert.Null(derived);
    }

    private static CriticalSourceEvidence.Assessment Assess(
        string span, string identity, decimal quantity, string uom)
        => CriticalSourceEvidence.Assess(
            [new("SourceSpan", span, null)],
            [new("ManufacturerPartNumber", identity)], quantity, uom);
}
