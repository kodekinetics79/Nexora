using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE APPROVAL PATH, PROVEN ON AN UPLOAD-DOOR LEAD.
///
/// The needs-review queue lists leads whose EmailIngest is flagged NeedsReview OR that
/// have NO EmailIngest at all — the second case being every lead that arrived through the
/// upload door. The submit gate, however, required an EmailIngest with ParseStatus
/// "NeedsReview". Upload-door leads were therefore offered for review and then refused at
/// submit with "This lead is no longer awaiting extraction review."
///
/// That is not a cosmetic defect. Approved reviews are the ONLY source of labelled ground
/// truth this product has; a closed approval path means the pilot generates zero
/// measurable evidence, and three months later the accuracy question is exactly where it
/// started. These tests hold the path open.
/// </summary>
public class UploadDoorReviewApprovalTests
{
    private const long Bu = 1;

    [Fact]
    public async Task An_upload_door_lead_with_no_email_ingest_can_be_approved()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedUploadDoorLead(seed, 300);
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            var response = await new LeadRepository(ctx).SubmitLeadReviewAsync(300, Bu,
                new LeadReviewSubmitDTO
                {
                    ExpectedVersion = 1,
                    Action = "approve",
                    Reason = "Checked against the uploaded workbook.",
                    Items = new()
                    {
                        new LeadItemReviewDTO
                        {
                            Id = 1, LineItemNo = "L1", Quantity = 4, ProductShortName = "Sealed Fitting"
                        }
                    }
                }, reviewedBy: "reviewer@example.com");
            Assert.NotNull(response);
        }

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Single(l => l.Id == 300);
        Assert.True(lead.CommercialFactsVerified);
        Assert.Equal("reviewer@example.com", lead.ReviewApprovedBy);
        Assert.Equal(2, lead.ReviewVersion);
    }

    [Fact]
    public async Task Approving_an_upload_door_lead_produces_corpus_labels()
    {
        // The point of unblocking the path: evidence accumulates.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedUploadDoorLead(seed, 301);
            seed.SaveChanges();
        }

        using (var ctx = db.ContextFor(Bu))
        {
            await new LeadRepository(ctx).SubmitLeadReviewAsync(301, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "approve",
                Reason = "Checked against the uploaded workbook.",
                Items = new()
                {
                    new LeadItemReviewDTO
                    {
                        Id = 1, LineItemNo = "L1", Quantity = 4, ProductShortName = "Sealed Fitting"
                    }
                }
            });
        }

        using var verify = db.ContextFor(Bu);
        Assert.NotEmpty(verify.Set<ExtractionCorpusEntry>().Where(e => e.LeadId == 301));
    }

    [Fact]
    public async Task An_already_approved_upload_door_lead_cannot_be_reviewed_again()
    {
        // The email door's guard is EmailIngest.ParseStatus; an upload-door lead has none,
        // so its terminal state must come from the lead itself or the gate would never close.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var lead = SeedUploadDoorLead(seed, 302);
            lead.CommercialFactsVerified = true;
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        await Assert.ThrowsAsync<LeadReviewConflictException>(() =>
            new LeadRepository(ctx).SubmitLeadReviewAsync(302, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Items = new() { new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 4 } }
            }));
    }

    [Fact]
    public async Task A_lead_with_no_ingest_and_no_source_document_is_not_reviewable()
    {
        // Opening the gate for missing EmailIngests must not open it for every hand-created
        // lead in the tenant. Reviewable means there is a document behind it.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            seed.Leads.Add(new Lead
            {
                Id = 303,
                Rfqno = "RFQ-303",
                BuyersName = "Hand Entered",
                RecDate = DateTime.UtcNow,
                LeadSource = "Manual",
                CreatedBy = "seed",
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = Bu,
                LeadItems = { Seed.LeadItem(9, "L1", 1) }
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        await Assert.ThrowsAsync<LeadReviewConflictException>(() =>
            new LeadRepository(ctx).SubmitLeadReviewAsync(303, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Items = new() { new LeadItemReviewDTO { Id = 9, LineItemNo = "L1", Quantity = 1 } }
            }));
    }

    [Fact]
    public async Task The_queue_that_offers_the_lead_and_the_gate_that_accepts_it_agree()
    {
        // The regression that caused this: the queue predicate at GetNeedsReviewLeadsAsync
        // and the submit gate disagreed about what "awaiting review" means. Anything the
        // queue offers must be submittable.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedUploadDoorLead(seed, 304);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        var (queued, _) = await repo.GetNeedsReviewLeadsAsync(1, 50, Bu);
        Assert.Contains(queued, l => l.Id == 304);

        var response = await repo.SubmitLeadReviewAsync(304, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { new LeadItemReviewDTO { Id = 1, LineItemNo = "L1", Quantity = 4 } }
        });
        Assert.NotNull(response);
    }

    /// <summary>A lead as the upload door leaves it: no EmailIngest, real document evidence.</summary>
    private static Lead SeedUploadDoorLead(ErpRfqAutomationContext context, long leadId)
    {
        Seed.EnsureBusinessUnit(context, Bu);
        var lead = new Lead
        {
            Id = leadId,
            Rfqno = $"RFQ-{leadId}",
            BuyersName = "Gulf Industrial Trading",
            RecDate = DateTime.UtcNow,
            LeadSource = "Manual Upload",
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Bu,
            EmailIngestsId = null,
            LeadItems = { Seed.LeadItem(1, "L1", 4, "Sealed Fitting") }
        };
        context.Leads.Add(lead);
        context.SaveChanges();

        var corpus = DocumentCorpus.Create(Bu, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        context.SaveChanges();

        var hash = new string('c', 64);
        var source = SourceDocument.Create(Bu, corpus.Id, hash, $"upload-{leadId}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "quarantine", $"tenant/{Bu}/upload-{leadId}", "v1", 512);
        source.ReleaseFromQuarantine("cleared", $"tenant/{Bu}/upload-{leadId}", "v1");
        context.Set<SourceDocument>().Add(source);
        context.SaveChanges();

        var occurrence = SourceDocumentOccurrence.Create(Bu, source.Id, corpus.Id,
            $"upload-door:{leadId}", "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        context.SaveChanges();

        var job = new ExtractionJob
        {
            SourceDocumentOccurrenceId = occurrence.Id,
            BatchId = corpus.BatchId,
            BusinessUnitId = Bu,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash,
            StoragePath = source.ObjectKey,
            FileName = source.OriginalFileName,
            FileType = "xlsx",
            Status = ExtractionStatus.Succeeded,
            ResultLeadId = leadId,
            CreatedOn = DateTime.UtcNow,
            UpdatedOn = DateTime.UtcNow
        };
        context.Set<ExtractionJob>().Add(job);
        context.SaveChanges();

        occurrence.BindExtractionJob(job.Id);
        occurrence.MarkProcessing();
        occurrence.MarkResolved();
        context.SaveChanges();

        return lead;
    }
}
