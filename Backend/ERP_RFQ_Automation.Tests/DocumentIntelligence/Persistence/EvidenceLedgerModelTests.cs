using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence.Persistence;

public sealed class EvidenceLedgerModelTests
{
    private sealed class EvidenceLedgerTestContext(DbContextOptions<EvidenceLedgerTestContext> options)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddEvidenceLedger();
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<EvidenceLedgerTestContext>()
            .UseNpgsql("Host=localhost;Database=evidence_model_only;Username=test;Password=test")
            .Options;
        using var context = new EvidenceLedgerTestContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Extension_RegistersAllSevenEvidenceEntities()
    {
        var model = BuildModel();
        var expected = new[]
        {
            typeof(DocumentCorpus), typeof(SourceDocument), typeof(DocumentPage), typeof(DocumentRegion),
            typeof(CanonicalInquiry), typeof(CanonicalLineItem), typeof(FieldEvidence)
        };

        Assert.All(expected, type => Assert.NotNull(model.FindEntityType(type)));
    }

    [Fact]
    public void SourceDocument_HasTenantHashAndObjectVersionUniqueness()
    {
        var entity = BuildModel().FindEntityType(typeof(SourceDocument))!;
        var indexes = entity.GetIndexes().ToDictionary(x => x.GetDatabaseName()!);

        Assert.True(indexes["ux_source_documents_tenant_hash"].IsUnique);
        Assert.Equal(new[] { "BusinessUnitId", "ContentHash" },
            indexes["ux_source_documents_tenant_hash"].Properties.Select(x => x.Name));
        Assert.True(indexes["ux_source_documents_object_version"].IsUnique);
        Assert.Equal(DeleteBehavior.Restrict, entity.GetForeignKeys().Single().DeleteBehavior);
    }

    [Fact]
    public void PageAndCanonicalLineNumbers_AreUniqueWithinTheirParents()
    {
        var model = BuildModel();
        var pageIndex = model.FindEntityType(typeof(DocumentPage))!.GetIndexes()
            .Single(x => x.GetDatabaseName() == "ux_document_pages_document_number");
        var lineIndex = model.FindEntityType(typeof(CanonicalLineItem))!.GetIndexes()
            .Single(x => x.GetDatabaseName() == "ux_canonical_line_items_inquiry_line");

        Assert.True(pageIndex.IsUnique);
        Assert.Equal(new[] { "DocumentId", "PageNumber" }, pageIndex.Properties.Select(x => x.Name));
        Assert.True(lineIndex.IsUnique);
        Assert.Equal(new[] { "InquiryId", "LineNumber" }, lineIndex.Properties.Select(x => x.Name));
    }

    [Fact]
    public void FieldEvidence_RequiresOneCanonicalTargetAndRetainsRegionRelationship()
    {
        var entity = BuildModel().FindEntityType(typeof(FieldEvidence))!;
        var targetConstraint = entity.GetCheckConstraints().Single(x => x.Name == "ck_field_evidence_target");
        var foreignKeys = entity.GetForeignKeys().ToDictionary(x => x.PrincipalEntityType.ClrType);

        Assert.Contains("inquiry_id IS NOT NULL", targetConstraint.Sql);
        Assert.Contains("line_item_id IS NOT NULL", targetConstraint.Sql);
        Assert.Equal(nameof(FieldEvidence.RegionId), foreignKeys[typeof(DocumentRegion)].Properties.Single().Name);
        Assert.Equal(DeleteBehavior.Restrict, foreignKeys[typeof(DocumentRegion)].DeleteBehavior);
    }

    [Fact]
    public void Statuses_AreStoredAsReadableStrings()
    {
        var model = BuildModel();
        var corpusStatus = model.FindEntityType(typeof(DocumentCorpus))!.FindProperty(nameof(DocumentCorpus.Status))!;
        var securityStatus = model.FindEntityType(typeof(SourceDocument))!
            .FindProperty(nameof(SourceDocument.SecurityStatus))!;

        Assert.Equal(typeof(string), corpusStatus.GetProviderClrType());
        Assert.Equal(typeof(string), securityStatus.GetProviderClrType());
        Assert.Equal(32, corpusStatus.GetMaxLength());
    }
}
