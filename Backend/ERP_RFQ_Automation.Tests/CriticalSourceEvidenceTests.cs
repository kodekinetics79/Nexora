using ERP_RFQ_Automation.CommercialCases.Participation;

namespace ERP_RFQ_Automation.Tests;

public sealed class CriticalSourceEvidenceTests
{
    [Fact]
    public void Exact_verified_prose_span_proves_identity_quantity_and_unit_together()
    {
        var assessment = Assess(
            "Line 1: Control valve, manufacturer part number VALVE-A, quantity 2 EA",
            "VALVE-A", 2m, "EA");

        Assert.True(assessment.Complete);
        Assert.Empty(assessment.Missing());
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
            ["VALVE-A"], 2m, "EA");

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
            ["VALVE-A"], 2m, "EA");

        Assert.True(assessment.Complete);
    }

    [Fact]
    public void Verified_span_derivation_preserves_exact_source_tokens()
    {
        var derived = CriticalSourceEvidence.DeriveFromVerifiedSpan(
            "Line 3: belt BELT-FG-1275, quantity 12.75 kg",
            [("ManufacturerPartNumber", "BELT-FG-1275")],
            12.75m,
            "KG");

        Assert.NotNull(derived);
        Assert.Equal("ManufacturerPartNumber", derived!.IdentityFieldName);
        Assert.Equal("BELT-FG-1275", derived.IdentityRawValue);
        Assert.Equal("12.75", derived.QuantityRawValue);
        Assert.Equal("kg", derived.UnitOfMeasureRawValue);
    }

    private static CriticalSourceEvidence.Assessment Assess(
        string span, string identity, decimal quantity, string uom)
        => CriticalSourceEvidence.Assess(
            [new("SourceSpan", span, null)],
            [identity], quantity, uom);
}
