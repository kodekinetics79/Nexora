using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Anchoring;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// X1. "Read 3 of about 42 lines" is recorded once, at extraction, as a PARTIAL_READ finding on
/// the run; Decide reads it back for the one line it shows. It used to live only in a free-text
/// remark nothing displayed, while the job said Succeeded.
/// </summary>
public sealed class LeadReadCompletenessTests
{
    private const long Tenant = 97_301;

    [Fact]
    public async Task A_partial_read_recorded_at_extraction_is_what_Decide_reads_back()
    {
        using var db = new TestDb();
        var leadId = await SeedLeadWithReadAsync(db, new PartialReadFact(3, 42, 28));

        await using var context = db.ContextFor(Tenant);
        var read = await LeadReadCompletenessController.ReadAsync(context, Tenant, leadId, CancellationToken.None);

        Assert.Equal(new LeadReadCompletenessDto(true, 3, 42, 28), read);
    }

    [Fact]
    public async Task A_complete_read_says_complete()
    {
        using var db = new TestDb();
        var leadId = await SeedLeadWithReadAsync(db, fact: null);

        await using var context = db.ContextFor(Tenant);
        var read = await LeadReadCompletenessController.ReadAsync(context, Tenant, leadId, CancellationToken.None);

        Assert.False(read.Partial);
    }

    [Fact]
    public async Task A_later_complete_read_of_the_same_request_clears_an_earlier_partial_one()
    {
        using var db = new TestDb();
        var leadId = await SeedLeadWithReadAsync(db, new PartialReadFact(3, 42, 28));
        await AddReadAsync(db, leadId, fact: null, inquiryNumber: 2);

        await using var context = db.ContextFor(Tenant);
        var read = await LeadReadCompletenessController.ReadAsync(context, Tenant, leadId, CancellationToken.None);

        Assert.False(read.Partial);
    }

    [Fact]
    public async Task Another_tenants_partial_read_is_never_reported()
    {
        using var db = new TestDb();
        var leadId = await SeedLeadWithReadAsync(db, new PartialReadFact(3, 42, 28));

        await using var context = db.ContextFor(null);
        var read = await LeadReadCompletenessController.ReadAsync(context, Tenant + 1, leadId, CancellationToken.None);

        Assert.False(read.Partial);
    }

    private static async Task<long> SeedLeadWithReadAsync(TestDb db, PartialReadFact? fact)
    {
        const long leadId = 97_310;
        await using (var seed = db.ContextFor(null))
        {
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
            Seed.Lead(seed, leadId, Tenant);
            seed.SaveChanges();
        }
        await AddReadAsync(db, leadId, fact, inquiryNumber: 1);
        return leadId;
    }

    /// <summary>One extraction of the request's document: corpus, document, job, run, inquiry and, when partial, the finding.</summary>
    private static async Task AddReadAsync(TestDb db, long leadId, PartialReadFact? fact, int inquiryNumber)
    {
        await using var context = db.ContextFor(null);
        var hash = new string((char)('a' + inquiryNumber), 64);
        var batch = Guid.NewGuid();
        var corpus = DocumentCorpus.Create(Tenant, batch, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();

        var job = new ExtractionJob
        {
            BatchId = batch, BusinessUnitId = Tenant, SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash, StoragePath = $"memory://read/{inquiryNumber}.pdf", FileName = "RFP 6000000034.pdf",
            FileType = "pdf", Status = ExtractionStatus.Succeeded, Attempts = 1, MaxAttempts = 5,
            NextAttemptAt = DateTime.UtcNow, ResultLeadId = leadId, CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        };
        context.Add(job);
        await context.SaveChangesAsync();

        var document = SourceDocument.Create(Tenant, corpus.Id, hash, "RFP 6000000034.pdf", "application/pdf",
            "memory", $"read/{inquiryNumber}.pdf", hash, 1024);
        document.ReleaseFromQuarantine("memory", $"read/{inquiryNumber}.pdf", hash);
        document.BindExtractionJob(job.Id);
        context.Add(document);
        await context.SaveChangesAsync();

        var run = ExtractionRun.Create(Tenant, document.Id, Guid.NewGuid(), job.Id, 1, "llm-unstructured/v2", "lead-extraction/v2");
        var inquiry = CanonicalInquiry.Create(Tenant, corpus.Id, inquiryNumber);
        inquiry.BindLead(leadId);
        context.AddRange(run, inquiry);
        await context.SaveChangesAsync();

        if (fact is not null)
        {
            var finding = ValidationFinding.ForInquiry(Tenant, run.Id, inquiry.Id, PartialReadFact.FindingCode,
                ValidationSeverity.Warning, fact.Describe());
            context.Add(finding);
            await context.SaveChangesAsync();
        }
    }
}
