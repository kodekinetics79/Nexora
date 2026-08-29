using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class EmailInquiryCommercialConflictDetectorTests
{
    public static IEnumerable<object[]> CriticalHeaderConflicts()
    {
        yield return ["rfq", (Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult>)(x => x with { Rfqno = "RFQ-200" })];
        yield return ["closing", (Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult>)(x => x with { BidClosingDate = "2026-08-01" })];
        yield return ["delivery-date", (Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult>)(x => x with { RequiredDeliveryDate = "2026-09-01" })];
        yield return ["delivery-location", (Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult>)(x => x with { DeliveryLocation = "Riyadh warehouse" })];
        yield return ["agreement", (Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult>)(x => x with { AgreementReference = "AGR-200" })];
    }

    [Fact]
    public void SameStablePartWithContradictoryQuantitiesAcrossBodyAndAttachmentIsAConflict()
    {
        var body = Ext.Item(0.95, "QA-FLT-50", 5) with
        {
            UnitOfMeasure = "EA"
        };
        var attachment = Ext.Item(0.95, "Filter element", 8) with
        {
            ItemMaterialCode = "QA-FLT-50",
            UnitOfMeasure = "each"
        };

        var count = EmailInquiryCommercialConflictDetector.Count([
            new(101, [body]),
            new(102, [attachment])
        ]);

        Assert.Equal(1, count);
    }

    [Fact]
    public void MissingValueAndEquivalentUomAreNotInventedIntoAConflict()
    {
        var body = Ext.Item(0.95, "QA-FLT-50") with
        {
            ManufacturerPartNumber = "QA-FLT-50",
            Quantity = null,
            UnitOfMeasure = "EA"
        };
        var attachment = body with { Quantity = 8, UnitOfMeasure = "each" };

        Assert.Equal(0, EmailInquiryCommercialConflictDetector.Count([
            new(101, [body]),
            new(102, [attachment])
        ]));
    }

    [Fact]
    public void SimilarDescriptionsWithoutAStableCodeDoNotCollapseDistinctLines()
    {
        var first = Ext.Item(0.95, "Air filter", 5);
        var second = Ext.Item(0.95, "Air filter", 8);

        Assert.Equal(0, EmailInquiryCommercialConflictDetector.Count([
            new(101, [first]),
            new(102, [second])
        ]));
    }

    [Theory]
    [MemberData(nameof(CriticalHeaderConflicts))]
    public void DifferentNonEmptyCriticalHeadersAcrossComponentsAreConflicts(
        string _,
        Func<ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult,
            ERP_RFQ_Automation.Services.Interfaces.LeadExtractionResult> change)
    {
        var body = Ext.Result([Ext.Item(0.95)], 0.95) with
        {
            RequiredDeliveryDate = "2026-08-15",
            DeliveryLocation = "Jeddah warehouse",
            AgreementReference = "AGR-100"
        };
        var attachment = change(body);

        Assert.Equal(1, EmailInquiryCommercialConflictDetector.CountHeaderConflicts([
            new(101, body),
            new(102, attachment)
        ]));
    }

    [Fact]
    public void MissingAndFormattingEquivalentCriticalHeadersRemainMergeable()
    {
        var body = Ext.Result([Ext.Item(0.95)], 0.95) with
        {
            Rfqno = " RFQ-001 ",
            BidClosingDate = "2026-07-30",
            RequiredDeliveryDate = null,
            DeliveryLocation = "  Jeddah   Main Warehouse ",
            AgreementReference = "AGR-100"
        };
        var attachment = body with
        {
            Rfqno = "rfq 001",
            BidClosingDate = "2026-07-30T00:00:00Z",
            RequiredDeliveryDate = "2026-08-15",
            DeliveryLocation = "jeddah main warehouse",
            AgreementReference = "AGR 100"
        };

        Assert.Equal(0, EmailInquiryCommercialConflictDetector.CountHeaderConflicts([
            new(101, body),
            new(102, attachment)
        ]));
    }
}
