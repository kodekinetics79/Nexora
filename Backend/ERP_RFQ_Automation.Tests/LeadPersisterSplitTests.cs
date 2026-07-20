using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
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
        FileType = "pdf"
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

    [Fact]
    public async Task SplitOutcome_PersistsOneLeadPerGroup_SharingOneIngest()
    {
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            await seedCtx.SaveChangesAsync();
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
    public async Task SingleOutcome_StillPersistsOneLead_WithInquiryType()
    {
        await using (var seedCtx = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seedCtx, 1);
            Seed.EmailConfig(seedCtx, 100, 1);
            await seedCtx.SaveChangesAsync();
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
                Assert.Equal("inbox100@example.com", lead.Clientemail);
                Assert.Contains("Subject: RFQ for pumps", lead.HeaderRemarks);

                var ingest = await assertCtx.EmailIngests.SingleAsync(e => e.Id == ingestId);
                Assert.Equal("Success", ingest.ParseStatus);
                Assert.NotNull(ingest.ParsedAt);
            }
        }
        finally
        {
            try { File.Delete(storagePath); } catch { }
            try { File.Delete(ExtractionJobMetadata.SidecarPath(storagePath, job.BusinessUnitId)); } catch { }
        }
    }
}
