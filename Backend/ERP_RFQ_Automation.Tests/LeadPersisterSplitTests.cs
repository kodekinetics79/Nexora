using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// LeadPersister invariants for the unified pipeline: a multi-inquiry outcome persists N
/// separate Leads (one per group) that SHARE one EmailIngest + the source evidence; the
/// email door's provenance sidecar links leads to the REAL pre-created ingest instead of
/// a synthetic one; the WP-BOQ inquiry classification lands on the Lead. Runs on the
/// TestDb (real model, SQLite in-memory, FKs + unique indexes enforced).
/// </summary>
public class LeadPersisterSplitTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static ExtractionJob Job(long bu = 1, string? storagePath = null) => new()
    {
        Id = 1,
        BatchId = Guid.NewGuid(),
        BusinessUnitId = bu,
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = new string('a', 64),
        StoragePath = storagePath ?? "/nonexistent/extraction/doc.pdf",
        FileName = "doc.pdf",
        FileType = "pdf",
        Attempts = 1
    };

    private static LeadExtractionResult Group(string rfq, int itemCount, string? inquiryType = null)
        => Ext.Result(Ext.Items(itemCount, 0.9), 0.9) with { Rfqno = rfq, InquiryType = inquiryType };

    private static ChunkedExtractionOutcome SplitOutcome(params LeadExtractionResult[] groups) => new()
    {
        Status = ExtractionOutcomeStatus.Ok,
        Result = groups[0] with { Items = groups.SelectMany(g => g.Items).ToList() },
        SplitResults = groups.ToList(),
        ExpectedItemCount = groups.Sum(g => g.Items.Count),
        ExtractedItemCount = groups.Sum(g => g.Items.Count)
    };

    private static async Task SeedAuthoritativeSourceAsync(ErpRfqAutomationContext context, ExtractionJob job)
    {
        var corpus = DocumentCorpus.Create(job.BusinessUnitId, job.BatchId, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(job.BusinessUnitId, corpus.Id, job.ContentHash,
            job.FileName ?? "document", "application/pdf", "test", $"lead-persister/{job.Id}", "v1", 1);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
        context.Add(source);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SplitOutcome_PersistsOneLeadPerGroup_SharingOneIngest()
    {
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            await seedCtx.SaveChangesAsync();
            await SeedAuthoritativeSourceAsync(seedCtx, Job());
        }

        long firstLeadId;
        await using (var ctx = _db.ContextFor(null))
        {
            var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>());
            firstLeadId = await persister.PersistAsync(
                Job(), SplitOutcome(Group("RFQ-A", 2, "mixed"), Group("RFQ-B", 3, "mixed")));
        }

        await using (var assertCtx = _db.ContextFor(null))
        {
            var leads = await assertCtx.Leads.Include(l => l.LeadItems)
                .OrderBy(l => l.Id).ToListAsync();

            Assert.Equal(2, leads.Count);
            Assert.Equal(firstLeadId, leads[0].Id);

            // One shared EmailIngest for the whole document.
            Assert.Equal(leads[0].EmailIngestsId, leads[1].EmailIngestsId);

            // Per-group conservation + identity.
            Assert.Equal("RFQ-A", leads[0].Rfqno);
            Assert.Equal(2, leads[0].LeadItems.Count);
            Assert.Equal(2, leads[0].NoOfLineItems);
            Assert.Equal("RFQ-B", leads[1].Rfqno);
            Assert.Equal(3, leads[1].LeadItems.Count);
            Assert.Equal(3, leads[1].NoOfLineItems);

            // Split provenance note.
            Assert.Contains("Split from a multi-inquiry document (group 1 of 2)", leads[0].HeaderRemarks);
            Assert.Contains("Split from a multi-inquiry document (group 2 of 2)", leads[1].HeaderRemarks);

            // WP-BOQ classification persisted on every split lead.
            Assert.All(leads, l => Assert.Equal("mixed", l.InquiryType));

            // Shared source evidence: one Attachment row per lead, same document.
            var attachments = await assertCtx.Attachments.ToListAsync();
            Assert.Equal(2, attachments.Count);
            Assert.All(attachments, a => Assert.Equal("doc.pdf", a.FileName));
            Assert.Equal(leads.Select(l => l.Id).OrderBy(x => x),
                attachments.Select(a => a.ParentId).OrderBy(x => x));
        }
    }

    [Fact]
    public async Task Governed_split_uses_distinct_logical_source_keys_and_keeps_both_inquiries()
    {
        await using (var seed = _db.ContextFor(1))
        {
            Seed.BusinessUnit(seed, 1); Seed.EmailConfig(seed, 100, 1); await seed.SaveChangesAsync();
            await SeedAuthoritativeSourceAsync(seed, Job());
        }
        var storagePath = Path.Combine(Path.GetTempPath(), $"split-identity-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(storagePath, "two inquiries");
        var job = Job(storagePath: storagePath);
        try
        {
            await new ExtractionJobMetadata { SourceOccurrenceId = "api-request-77", FromEmail = "buyer@customer.test" }.SaveAsync(storagePath, 1);
            await using var context = _db.ContextFor(1);
            var identity = new LeadIdentityApplicationService(context);
            var persister = new LeadPersister(context, new NoopLogger<LeadPersister>(), leadIdentity: identity);
            var outcome = SplitOutcome(Group("RFQ-A", 1), Group("RFQ-B", 1));
            outcome = new ChunkedExtractionOutcome { Status = outcome.Status, Result = outcome.Result, SplitResults = outcome.SplitResults,
                ExpectedItemCount = outcome.ExpectedItemCount, ExtractedItemCount = outcome.ExtractedItemCount, AiProviderClass = AiProviderClass.Local };
            await persister.PersistAsync(job, outcome);

            Assert.Equal(2, await context.Leads.CountAsync());
            var sourceIds = await context.Set<LeadIngestionOccurrence>().OrderBy(x => x.Id).Select(x => x.ExternalSourceId).ToListAsync();
            Assert.Equal(new[] { "api-request-77:inquiry:1", "api-request-77:inquiry:2" }, sourceIds);
        }
        finally
        {
            File.Delete(storagePath);
            File.Delete(ExtractionJobMetadata.SidecarPath(storagePath, 1));
        }
    }

    [Fact]
    public void One_source_receipt_can_link_to_multiple_logical_lead_occurrences()
    {
        using var context = _db.ContextFor(1);
        var entity = context.Model.FindEntityType(typeof(LeadIngestionOccurrence))!;
        var sourceBridge = entity.GetIndexes().Single(index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(LeadIngestionOccurrence.BusinessUnitId), nameof(LeadIngestionOccurrence.SourceDocumentOccurrenceId)]));

        Assert.False(sourceBridge.IsUnique);
    }

    [Fact]
    public async Task SingleOutcome_StillPersistsOneLead_WithInquiryType()
    {
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            await seedCtx.SaveChangesAsync();
            await SeedAuthoritativeSourceAsync(seedCtx, Job());
        }

        await using var ctx = _db.ContextFor(null);
        var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>());
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Group("RFQ-9", 2, "service"),
            ExpectedItemCount = 2,
            ExtractedItemCount = 2
        };

        var leadId = await persister.PersistAsync(Job(), outcome);

        var lead = await ctx.Leads.Include(l => l.LeadItems).SingleAsync(l => l.Id == leadId);
        Assert.Equal("RFQ-9", lead.Rfqno);
        Assert.Equal(2, lead.LeadItems.Count);
        Assert.Equal("service", lead.InquiryType);
        Assert.DoesNotContain("Split from a multi-inquiry", lead.HeaderRemarks ?? "");
    }

    [Fact]
    public async Task EmailSidecar_LinksLeadToPreCreatedIngest_AndUpdatesItsStatus()
    {
        long ingestId;
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            var ingest = Seed.EmailIngest(seedCtx, 500, 100, "Queued");
            await seedCtx.SaveChangesAsync();
            await SeedAuthoritativeSourceAsync(seedCtx, Job());
            ingestId = ingest.Id;
        }

        // Real stored file + provenance sidecar, exactly as the email door writes them.
        var storagePath = Path.Combine(Path.GetTempPath(), $"persister-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(storagePath, "Subject: RFQ\n\nbody");
        var job = Job(storagePath: storagePath);
        try
        {
            await new ExtractionJobMetadata
            {
                EmailIngestId = ingestId,
                FromEmail = "buyer@customer.com",
                Subject = "RFQ for pumps",
                ClientEmail = "inbox100@example.com",
                LeadSource = "Email",
                EmailSource = "Text Only"
            }.SaveAsync(storagePath, job.BusinessUnitId);

            long leadId;
            await using (var ctx = _db.ContextFor(null))
            {
                var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>());
                leadId = await persister.PersistAsync(job, new ChunkedExtractionOutcome
                {
                    Status = ExtractionOutcomeStatus.Ok,
                    Result = Group("RFQ-77", 1),
                    ExpectedItemCount = 1,
                    ExtractedItemCount = 1
                });
            }

            await using (var assertCtx = _db.ContextFor(null))
            {
                var lead = await assertCtx.Leads.SingleAsync(l => l.Id == leadId);
                // Linked to the REAL pre-created ingest, not a synthetic one.
                Assert.Equal(ingestId, lead.EmailIngestsId);
                Assert.Equal(1, await assertCtx.EmailIngests.CountAsync());
                // Email-door parity fields from the sidecar.
                Assert.Equal("Email", lead.LeadSource);
        Assert.Equal("buyer@customer.com", lead.Clientemail);
                Assert.Contains("Subject: RFQ for pumps", lead.HeaderRemarks);

                var ingest = await assertCtx.EmailIngests.SingleAsync(e => e.Id == ingestId);
                Assert.Equal("NeedsReview", ingest.ParseStatus);
                Assert.NotNull(ingest.ParsedAt);
            }
        }
        finally
        {
            try { File.Delete(storagePath); } catch { }
            try { File.Delete(ExtractionJobMetadata.SidecarPath(storagePath, job.BusinessUnitId)); } catch { }
        }
    }

    [Fact]
    public async Task AtomicPersistence_RollsBackLeadWhenFencedCompletionFails()
    {
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            await seedCtx.SaveChangesAsync();
            await SeedAuthoritativeSourceAsync(seedCtx, Job());
        }

        await using (var context = _db.ContextFor(null))
        {
            var persister = new LeadPersister(context, new NoopLogger<LeadPersister>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => persister.PersistAndCompleteAsync(
                Job(),
                SplitOutcome(Group("RFQ-ROLLBACK", 1)),
                new CompletionRejectingQueue(),
                "worker-a",
                1,
                TimeSpan.FromMinutes(5)));
        }

        await using var assertContext = _db.ContextFor(null);
        Assert.Empty(await assertContext.Leads.ToListAsync());
        Assert.Empty(await assertContext.EmailIngests.ToListAsync());
    }

    private sealed class CompletionRejectingQueue : IExtractionQueue
    {
        public Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
