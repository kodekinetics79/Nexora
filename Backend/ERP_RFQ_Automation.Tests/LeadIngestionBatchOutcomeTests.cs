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
}
