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
    public void Extension_RegistersCompleteAuthoritativeEvidenceGraph()
    {
        var model = BuildModel();
        var expected = new[]
        {
            typeof(DocumentCorpus), typeof(SourceDocument), typeof(DocumentPage), typeof(DocumentRegion),
            typeof(SourceDocumentOccurrence), typeof(ExtractionRun), typeof(CanonicalInquiry),
            typeof(CanonicalLineItem), typeof(ValidationFinding), typeof(FieldEvidence)
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
        Assert.Equal(new[] { nameof(FieldEvidence.BusinessUnitId), nameof(FieldEvidence.RegionId) },
            foreignKeys[typeof(DocumentRegion)].Properties.Select(x => x.Name));
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

    [Fact]
    public void Occurrence_HasTenantIdempotencyAndRestrictedParents()
    {
        var entity = BuildModel().FindEntityType(typeof(SourceDocumentOccurrence))!;
        var index = entity.GetIndexes().Single(x =>
            x.GetDatabaseName() == "ux_source_document_occurrences_tenant_idempotency");

        Assert.True(index.IsUnique);
        Assert.Equal(new[] { "BusinessUnitId", "IdempotencyKey" }, index.Properties.Select(x => x.Name));
        Assert.Equal(2, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.Equal("jsonb", entity.FindProperty(nameof(SourceDocumentOccurrence.SourceMetadataJson))!.GetColumnType());
    }

    [Fact]
    public void ExtractionRun_HasStableRunIdentityAttemptUniquenessAndLifecycleConstraints()
    {
        var entity = BuildModel().FindEntityType(typeof(ExtractionRun))!;
        var attempt = entity.GetIndexes().Single(x =>
            x.GetDatabaseName() == "ux_extraction_runs_tenant_job_attempt");

        Assert.True(attempt.IsUnique);
        Assert.Equal(new[] { "BusinessUnitId", "ExtractionJobId", "AttemptNumber" },
            attempt.Properties.Select(x => x.Name));
        Assert.Contains(entity.GetKeys(), key => key.Properties.Select(x => x.Name).SequenceEqual(new[] { "RunId" }));
        Assert.Contains(entity.GetCheckConstraints(), constraint => constraint.Name == "ck_extraction_runs_counts");
        Assert.Contains(entity.GetCheckConstraints(), constraint => constraint.Name == "ck_extraction_runs_failure");
    }

    [Fact]
    public void SpreadsheetEvidence_MapsCoordinatesAndDeterministicEvidenceKey()
    {
        var model = BuildModel();
        var page = model.FindEntityType(typeof(DocumentPage))!;
        var region = model.FindEntityType(typeof(DocumentRegion))!;
        var evidence = model.FindEntityType(typeof(FieldEvidence))!;

        Assert.Equal("page_kind", page.FindProperty(nameof(DocumentPage.PageKind))!.GetColumnName());
        Assert.Equal("source_address", region.FindProperty(nameof(DocumentRegion.SourceAddress))!.GetColumnName());
        Assert.Equal("row_number", region.FindProperty(nameof(DocumentRegion.RowNumber))!.GetColumnName());
        Assert.True(evidence.GetIndexes().Single(x =>
            x.GetDatabaseName() == "ux_field_evidence_tenant_key").IsUnique);
        Assert.Equal("jsonb", evidence.FindProperty(nameof(FieldEvidence.TransformationsJson))!.GetColumnType());
        Assert.Contains(evidence.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ExtractionRun) &&
            foreignKey.PrincipalKey.Properties.Select(x => x.Name)
                .SequenceEqual(new[] { nameof(ExtractionRun.BusinessUnitId), nameof(ExtractionRun.RunId) }));
    }

    [Fact]
    public void ValidationFinding_HasOptionalGroundingAndExactlyOneRunParent()
    {
        var entity = BuildModel().FindEntityType(typeof(ValidationFinding))!;
        var constraint = entity.GetCheckConstraints().Single(x => x.Name == "ck_validation_findings_target");
        var foreignKeys = entity.GetForeignKeys().ToArray();

        Assert.Contains("NOT (inquiry_id IS NOT NULL AND line_item_id IS NOT NULL)", constraint.Sql);
        Assert.Contains(foreignKeys, foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ExtractionRun));
        Assert.Contains(foreignKeys, foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(DocumentRegion));
        Assert.All(foreignKeys, foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [Fact]
    public void CanonicalProjectionFields_AreMappedForLeadAndValidationBinding()
    {
        var model = BuildModel();
        var inquiry = model.FindEntityType(typeof(CanonicalInquiry))!;
        var line = model.FindEntityType(typeof(CanonicalLineItem))!;

        Assert.Equal("received_date", inquiry.FindProperty(nameof(CanonicalInquiry.ReceivedDate))!.GetColumnName());
        Assert.Equal("bid_closing_date", inquiry.FindProperty(nameof(CanonicalInquiry.BidClosingDate))!.GetColumnName());
        Assert.Equal("lead_item_id", line.FindProperty(nameof(CanonicalLineItem.LeadItemId))!.GetColumnName());
        Assert.Equal("validation_status", line.FindProperty(nameof(CanonicalLineItem.ValidationStatus))!.GetColumnName());
        Assert.Contains(line.GetCheckConstraints(), constraint => constraint.Name == "ck_canonical_line_items_currency");
    }

    [Fact]
    public void EvidenceGraph_ForeignKeysAreTenantQualified()
    {
        var model = BuildModel();
        var tenantEntities = new[]
        {
            typeof(SourceDocument), typeof(SourceDocumentOccurrence), typeof(ExtractionRun),
            typeof(DocumentPage), typeof(DocumentRegion), typeof(CanonicalInquiry),
            typeof(CanonicalLineItem), typeof(ValidationFinding), typeof(FieldEvidence)
        };

        foreach (var type in tenantEntities)
        {
            var entity = model.FindEntityType(type)!;
            Assert.All(entity.GetForeignKeys(), foreignKey =>
                Assert.Equal(nameof(SourceDocument.BusinessUnitId), foreignKey.Properties[0].Name));
        }
    }
}
