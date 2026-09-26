using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the batch page can SAY about a document once its outcome is known. A day-one rep who
/// uploads the same RFQ twice used to watch "Identifying the customer" spin forever, because the
/// batch read model classified the file <c>ExactDuplicate</c> and then said nothing about which
/// inquiry it duplicated. These tests pin the batch endpoint to carrying the original.
/// </summary>
public sealed class LeadIngestionBatchOutcomeTests
{
    private static User Rep(ErpRfqAutomationContext ctx, long id, long bu, string first, string last)
    {
        var user = new User
        {
            Id = id, Buid = bu, IsActive = true, FirstName = first, LastName = last,
            Email = $"{first.ToLowerInvariant()}.{id}@nexora.test", PasswordHash = "x", ImageUrl = "", CreatedBy = "test",
        };
        ctx.Users.Add(user);
        return user;
    }

    private static LeadIngestionBatch Batch(ErpRfqAutomationContext ctx, long bu)
    {
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(), BusinessUnitId = bu, SourceChannel = "ManualUpload", CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        ctx.Add(batch);
        return batch;
    }

    private static async Task<SourceDocumentOccurrence> IntakeAsync(
        ErpRfqAutomationContext ctx, long bu, Guid batchId, string hash, string fileName)
    {
        var corpus = DocumentCorpus.Create(bu, batchId, CorpusSourceType.ManualUpload);
        ctx.Add(corpus);
        await ctx.SaveChangesAsync();
        var source = SourceDocument.Create(bu, corpus.Id, hash, fileName, "application/pdf", "evidence",
            $"quarantine/{fileName}", "v1", 512);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
        ctx.Add(source);
        await ctx.SaveChangesAsync();
        var intake = SourceDocumentOccurrence.Create(bu, source.Id, corpus.Id, Guid.NewGuid().ToString("N"),
            $"{{\"fileName\":\"{fileName}\"}}");
        ctx.Add(intake);
        await ctx.SaveChangesAsync();
        return intake;
    }

    private static LeadIngestionOccurrence Occurrence(long bu, Guid batchId, long leadId,
        LeadOccurrenceClassification classification, SourceDocumentOccurrence? intake = null, string? fileName = null) =>
        new()
        {
            BusinessUnitId = bu, BatchId = batchId, LeadId = leadId, OriginalFileName = fileName,
            SourceDocumentId = intake?.SourceDocumentId, SourceDocumentOccurrenceId = intake?.Id,
            SourceChannel = "ManualUpload", IdempotencyKey = Guid.NewGuid().ToString("N"),
            LogicalInquiryFingerprint = Guid.NewGuid().ToString("N"), Classification = classification,
            Confidence = 1m, ProcessingPath = LeadProcessingPath.Deterministic, IngestedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow, ActorId = "test", CorrelationId = Guid.NewGuid().ToString("N"),
        };

    [Fact]
    public async Task An_exact_duplicate_caught_at_intake_names_the_original_inquiry_and_its_owner()
    {
        const long bu = 7_310;
        using var db = new TestDb();
        await using var ctx = db.ContextFor(bu);
        Seed.BusinessUnit(ctx, bu);
        Rep(ctx, 7_311, bu, "Oze", "Khan");
        await ctx.SaveChangesAsync();

        // First upload: read, reconciled into a lead, assigned to Oze.
        var firstBatch = Batch(ctx, bu);
        await ctx.SaveChangesAsync();
        var original = await IntakeAsync(ctx, bu, firstBatch.Id, new string('a', 64), "AJP-RFQ-2026-0917.pdf");
        var lead = Seed.Lead(ctx, 7_312, bu);
        lead.Rfqno = "AJP-RFQ-2026-0917";
        lead.AssignTo = 7_311;
        await ctx.SaveChangesAsync();
        ctx.Add(Occurrence(bu, firstBatch.Id, lead.Id, LeadOccurrenceClassification.New, original));
        await ctx.SaveChangesAsync();

        // Second upload of the same bytes: stopped at intake by the hash, never reconciled.
        var secondBatch = Batch(ctx, bu);
        await ctx.SaveChangesAsync();
        var repeat = await IntakeAsync(ctx, bu, secondBatch.Id, new string('a', 64), "AJP-RFQ-2026-0917 (1).pdf");
        repeat.MarkExactDuplicateCandidate(original.Id, requiresRescan: false);
        repeat.ConfirmExactDuplicate(processingReused: true);
        await ctx.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(ctx).GetBatchAsync(bu, secondBatch.Id);

        Assert.NotNull(result);
        var item = Assert.Single(result.Items);
        Assert.Equal("ExactDuplicate", item.Classification);
        Assert.Null(item.LeadId);
        Assert.NotNull(item.DuplicateOf);
        Assert.Equal(lead.Id, item.DuplicateOf!.LeadId);
        Assert.Equal("AJP-RFQ-2026-0917", item.DuplicateOf.RfqNo);
        Assert.Equal("Oze Khan", item.DuplicateOf.OwnerName);
    }

    [Fact]
    public async Task A_reconciled_exact_duplicate_row_names_its_own_lead_as_the_original()
    {
        const long bu = 7_320;
        using var db = new TestDb();
        await using var ctx = db.ContextFor(bu);
        Seed.BusinessUnit(ctx, bu);
        Rep(ctx, 7_321, bu, "Sara", "Bin Ali");
        var batch = Batch(ctx, bu);
        await ctx.SaveChangesAsync();
        var lead = Seed.Lead(ctx, 7_322, bu);
        lead.AssignTo = 7_321;
        await ctx.SaveChangesAsync();
        ctx.Add(Occurrence(bu, batch.Id, lead.Id, LeadOccurrenceClassification.ExactDuplicate, fileName: "again.pdf"));
        await ctx.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(ctx).GetBatchAsync(bu, batch.Id);

        var item = Assert.Single(result!.Items);
        Assert.Equal("ExactDuplicate", item.Classification);
        Assert.NotNull(item.DuplicateOf);
        Assert.Equal(lead.Id, item.DuplicateOf!.LeadId);
        Assert.Equal("RFQ-7322", item.DuplicateOf.RfqNo);
        Assert.Equal("Sara Bin Ali", item.DuplicateOf.OwnerName);
    }

    [Fact]
    public void Revision_differences_compact_to_line_field_from_to_in_a_reps_words()
    {
        var differences = new[]
        {
            new LeadRevisionDifference { Id = 1, ChangeType = LeadRevisionChangeType.Unchanged, Scope = "Field", Path = "$.buyersName", PreviousValueJson = "\"Aramco\"", CurrentValueJson = "\"Aramco\"" },
            new LeadRevisionDifference { Id = 2, ChangeType = LeadRevisionChangeType.Modified, Scope = "Field", Path = "$.bidClosingDate", PreviousValueJson = "\"2026-10-01T00:00:00Z\"", CurrentValueJson = "\"2026-10-08T00:00:00Z\"" },
            new LeadRevisionDifference { Id = 3, ChangeType = LeadRevisionChangeType.Modified, Scope = "Field", Path = "$.commercialCaseId", PreviousValueJson = "1", CurrentValueJson = "2" },
            new LeadRevisionDifference { Id = 4, ChangeType = LeadRevisionChangeType.Modified, Scope = "Line", Path = "$.items[\"2\"]",
                PreviousValueJson = "{\"line\":\"2\",\"part\":\"VALVE-A\",\"Quantity\":20,\"quantity\":20.0,\"unitOfMeasure\":\"EA\"}",
                CurrentValueJson = "{\"line\":\"2\",\"part\":\"VALVE-A\",\"Quantity\":35,\"quantity\":35.0,\"unitOfMeasure\":\"EA\"}" },
            new LeadRevisionDifference { Id = 5, ChangeType = LeadRevisionChangeType.Modified, Scope = "Line", Path = "$.items[\"4\"]",
                PreviousValueJson = "{\"lineItemNo\":\"4\",\"quantity\":1500}", CurrentValueJson = "{\"lineItemNo\":\"4\",\"quantity\":2000}" },
            new LeadRevisionDifference { Id = 6, ChangeType = LeadRevisionChangeType.Added, Scope = "Line", Path = "$.items[\"ordinal:5\"]",
                PreviousValueJson = null, CurrentValueJson = "{\"part\":\"GASKET-9\",\"quantity\":10}" },
        };

        var changes = LeadIdentityApplicationService.CompactChanges(differences);

        Assert.Equal(
            [("closing date", (string?)null, "2026-10-01", "2026-10-08"), ("qty", "2", "20", "35"), ("qty", "4", "1500", "2000"), ("line", "5", null, "added")],
            changes.Select(c => (c.Field, c.Line, c.From, c.To)).ToArray());
        Assert.DoesNotContain(changes, c => c.Field.Contains("commercialCase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_revision_of_a_lead_that_became_an_rfq_carries_the_changes_and_the_rfq()
    {
        const long bu = 7_330;
        using var db = new TestDb();
        await using var ctx = db.ContextFor(bu);
        Seed.BusinessUnit(ctx, bu);
        Rep(ctx, 7_331, bu, "Oze", "Khan");
        var batch = Batch(ctx, bu);
        await ctx.SaveChangesAsync();
        var lead = Seed.Lead(ctx, 7_332, bu);
        lead.Rfqno = "AJP-RFQ-2026-0917";
        lead.AssignTo = 7_331;
        await ctx.SaveChangesAsync();
        var occurrence = Occurrence(bu, batch.Id, lead.Id, LeadOccurrenceClassification.Revision, fileName: "AJP-RFQ-2026-0917 rev2.pdf");
        ctx.Add(occurrence);
        await ctx.SaveChangesAsync();
        var revision = new LeadRevision
        {
            BusinessUnitId = bu, LeadId = lead.Id, RevisionNumber = 2, EstablishedByOccurrenceId = occurrence.Id,
            LogicalInquiryFingerprint = new string('d', 64), SnapshotJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "worker", ProcessingPath = LeadProcessingPath.Deterministic,
        };
        revision.Differences.Add(new LeadRevisionDifference
        {
            BusinessUnitId = bu, ChangeType = LeadRevisionChangeType.Modified, Scope = "Line", Path = "$.items[\"2\"]",
            PreviousValueJson = "{\"line\":\"2\",\"quantity\":20}", CurrentValueJson = "{\"line\":\"2\",\"quantity\":35}",
        });
        revision.Differences.Add(new LeadRevisionDifference
        {
            BusinessUnitId = bu, ChangeType = LeadRevisionChangeType.Unchanged, Scope = "Field", Path = "$.buyersName",
            PreviousValueJson = "\"Acme\"", CurrentValueJson = "\"Acme\"",
        });
        ctx.Add(revision);
        await ctx.SaveChangesAsync();
        occurrence.LeadRevisionId = revision.Id;
        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = 2;
        ctx.Rfqs.Add(new Rfq
        {
            Id = 7_333, Rfqno = "RFQ-2026-000031", RecDate = DateTime.UtcNow, BusinessUnitId = bu, LeadId = lead.Id,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(ctx).GetBatchAsync(bu, batch.Id);

        var item = Assert.Single(result!.Items);
        Assert.Equal("Revision", item.Classification);
        Assert.Equal("AJP-RFQ-2026-0917", item.CustomerReference);
        var change = Assert.Single(item.Changes);
        Assert.Equal(("2", "qty", "20", "35"), (change.Line, change.Field, change.From, change.To));
        Assert.NotNull(item.Rfq);
        Assert.Equal(7_333, item.Rfq!.RfqId);
        Assert.Equal("RFQ-2026-000031", item.Rfq.RfqNo);
        Assert.Equal("Oze Khan", item.Rfq.OwnerName);
    }

    [Fact]
    public async Task A_lead_with_no_rfq_carries_no_rfq_link_so_the_page_can_still_offer_decide()
    {
        const long bu = 7_340;
        using var db = new TestDb();
        await using var ctx = db.ContextFor(bu);
        Seed.BusinessUnit(ctx, bu);
        var batch = Batch(ctx, bu);
        await ctx.SaveChangesAsync();
        var lead = Seed.Lead(ctx, 7_342, bu);
        await ctx.SaveChangesAsync();
        ctx.Add(Occurrence(bu, batch.Id, lead.Id, LeadOccurrenceClassification.New, fileName: "new.pdf"));
        await ctx.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(ctx).GetBatchAsync(bu, batch.Id);

        var item = Assert.Single(result!.Items);
        Assert.Null(item.Rfq);
        Assert.Empty(item.Changes);
    }
}
