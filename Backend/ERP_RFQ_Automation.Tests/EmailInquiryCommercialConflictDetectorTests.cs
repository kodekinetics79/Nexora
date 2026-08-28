using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class EmailInquiryCommercialConflictDetectorTests
{
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
}
