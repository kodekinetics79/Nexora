using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// LeadRepository.SubmitLeadReviewAsync is the persistence heart of the review workbench:
/// it loads the lead aggregate TRACKED and flushes header edits, item update/insert/delete
/// and its immutable audit record in a single SaveChanges. These tests exercise the real
/// repository against a relational SQLite database (no mocking of EF).
/// </summary>
public class LeadReviewUpsertTests
{
    private const long Bu = 1;

    private static LeadItemReviewDTO ItemDto(long? id, string? name, int? qty, string? lineNo = null)
        => new() { Id = id, ProductShortName = name, Quantity = qty, LineItemNo = lineNo };

    private static void SeedAuthoritativeEvidence(ErpRfqAutomationContext context, long leadId)
    {
        var corpus = DocumentCorpus.Create(Bu, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        context.SaveChanges();

        var hash = new string('a', 64);
        var source = SourceDocument.Create(Bu, corpus.Id, hash, $"lead-{leadId}.pdf",
            "application/pdf", "quarantine", $"tenant/{Bu}/lead-{leadId}", "v1", 128);
        source.ReleaseFromQuarantine("cleared", $"tenant/{Bu}/lead-{leadId}", "v1");
        context.Set<SourceDocument>().Add(source);
        context.SaveChanges();

        var occurrence = SourceDocumentOccurrence.Create(Bu, source.Id, corpus.Id,
            $"lead-review:{leadId}", "{}");
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
            FileType = "pdf",
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
    }

    [Fact]
    public async Task RequestClarification_IsAuditedWithoutChangingLifecycle()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var seededLead = Seed.Lead(seed, 90, Bu, items: new[] { Seed.LeadItem(9, "Valve", 1) });
            seededLead.LeadItems.Single().Quantity = null;
            seed.SaveChanges();
        }

        using var context = db.ContextFor(Bu);
        var result = await new LeadRepository(context).RequestClarificationAsync(
            90, Bu, new LeadClarificationRequestDTO
            {
                ExpectedReviewVersion = 1,
                Note = "Please confirm the requested quantity."
            }, "sales@example.test");

        Assert.NotNull(result);
        context.ChangeTracker.Clear();
        var lead = await context.Leads.SingleAsync(item => item.Id == 90);
        var audit = await context.Set<LeadReviewAudit>().SingleAsync();
        Assert.Null(lead.LeadStatusId);
        Assert.True(lead.RequiresCommercialReview);
        Assert.False(lead.CommercialFactsVerified);
        Assert.Equal(2, lead.ReviewVersion);
        Assert.Equal("clarification", audit.Action);
        Assert.Equal("sales@example.test", audit.ReviewedBy);
        Assert.Equal("Please confirm the requested quantity.", audit.Reason);
        Assert.Equal(1, audit.FromVersion);
        Assert.Equal(2, audit.ToVersion);
    }

    [Fact]
    public async Task RequestClarification_RejectsStaleVersionWithoutAudit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 91, Bu);
            lead.ReviewVersion = 2;
            seed.SaveChanges();
        }

        using var context = db.ContextFor(Bu);
        await Assert.ThrowsAsync<LeadReviewConflictException>(() => new LeadRepository(context)
            .RequestClarificationAsync(91, Bu, new LeadClarificationRequestDTO
            {
                ExpectedReviewVersion = 1,
                Note = "Please confirm delivery terms."
            }, "sales@example.test"));
        Assert.Empty(context.Set<LeadReviewAudit>());
    }

    [Fact]
    public async Task RequestClarification_CrossTenantFailsClosed()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 92, Bu);
            seed.SaveChanges();
        }

        using var foreign = db.ContextFor(Bu + 1);
        var result = await new LeadRepository(foreign).RequestClarificationAsync(
            92, Bu + 1, new LeadClarificationRequestDTO
            {
                ExpectedReviewVersion = 1,
                Note = "Please confirm currency."
            }, "foreign@example.test");
        Assert.Null(result);
        Assert.Empty(foreign.Set<LeadReviewAudit>().IgnoreQueryFilters());
    }

    [Fact]
    public async Task ExistingItem_CreatesANewProjectionWithoutMutatingHistory()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[]
            {
                Seed.LeadItem(1, "L1", 2, "orig-A"),
                Seed.LeadItem(2, "L2", 5, "orig-B"),
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new()
            {
                ItemDto(1, "Updated Name", 99),
                ItemDto(2, "L2", 5),
            }
        });

        using var verify = db.ContextFor(Bu);
        var currentItem1 = verify.LeadItems.Single(i => i.ProductShortName == "Updated Name");
        Assert.NotEqual(1, currentItem1.Id);
        Assert.Equal(1, currentItem1.EvidenceSourceLeadItemId);
        Assert.Equal("Updated Name", currentItem1.ProductShortName);
        Assert.Equal(99, currentItem1.Quantity);
        Assert.Equal(2, verify.LeadItems.Count(i => i.LeadId == 100));
        var historical = verify.LeadItems.IgnoreQueryFilters().Single(i => i.Id == 1);
        Assert.False(historical.IsCurrentRevisionProjection);
        Assert.Equal("orig-A", historical.ProductShortName);
        Assert.Equal(2, historical.Quantity);
    }

    [Fact]
    public async Task NewItem_WithNullId_IsInserted()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 2) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new()
            {
                ItemDto(1, "L1", 2),
                ItemDto(null, "Brand New", 7),
            }
        });

        using var verify = db.ContextFor(Bu);
        var items = verify.LeadItems.Where(i => i.LeadId == 100).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ProductShortName == "Brand New" && i.Quantity == 7 && i.Id != 1);
        using var after = JsonDocument.Parse(verify.Set<LeadReviewAudit>().Single().AfterJson);
        var auditedIds = after.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt64()).ToArray();
        Assert.All(auditedIds, id => Assert.True(id > 0));
        Assert.Equal(auditedIds.Length, auditedIds.Distinct().Count());
    }

    [Fact]
    public async Task OmittedItem_IsAbsentFromCurrentProjectionButRetainedInHistory()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[]
            {
                Seed.LeadItem(1, "L1", 1),
                Seed.LeadItem(2, "L2", 1),
                Seed.LeadItem(3, "L3", 1),
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1), ItemDto(2, "L2", 1) } // item 3 omitted
        });

        using var verify = db.ContextFor(Bu);
        var current = verify.LeadItems.Where(i => i.LeadId == 100).OrderBy(i => i.ProductShortName).ToList();
        Assert.Equal(new[] { "L1", "L2" }, current.Select(x => x.ProductShortName));
        Assert.All(current, item => Assert.True(item.IsCurrentRevisionProjection));
        Assert.DoesNotContain(current, item => item.ProductShortName == "L3");
        var historical = verify.LeadItems.IgnoreQueryFilters().Where(i => i.LeadId == 100).ToList();
        Assert.Contains(historical, item => item.Id == 3 && !item.IsCurrentRevisionProjection);
        Assert.Equal(5, historical.Count);
    }

    [Fact]
    public async Task NoOfLineItems_IsRecomputed_AcrossInsertAndDelete()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[]
            {
                Seed.LeadItem(1, "L1", 1),
                Seed.LeadItem(2, "L2", 1),
                Seed.LeadItem(3, "L3", 1),
            });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        // keep 1, delete 2 & 3, add two new -> final count 3
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1), ItemDto(null, "N1", 1), ItemDto(null, "N2", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Single(l => l.Id == 100);
        Assert.Equal(3, lead.NoOfLineItems);
        Assert.Equal(3, verify.LeadItems.Count(i => i.LeadId == 100));
    }

    [Fact]
    public async Task Save_KeepsParseStatusNeedsReview()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Include(l => l.EmailIngests).Single(l => l.Id == 100);
        Assert.Equal("NeedsReview", lead.EmailIngests.ParseStatus);
    }

    [Fact]
    public async Task Approve_ClearsReviewWithoutBypassingLifecycle()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 1) });
            SeedAuthoritativeEvidence(seed, 100);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "approve",
            Reason = "Verified against source document.",
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var reviewed = verify.Leads.Include(l => l.EmailIngests).Single(l => l.Id == 100);
        Assert.Null(reviewed.LeadStatusId);
        Assert.Equal("Success", reviewed.EmailIngests.ParseStatus);
    }

    [Fact]
    public async Task Approving_a_reviewer_chosen_client_teaches_the_identity_store()
    {
        // The learning loop's ONLY trigger: action == approve AND an explicitly supplied
        // customer. It runs inside the review's own transaction, so what the reviewer taught
        // commits with the review that taught it.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, 700, Bu, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                buyersName: "3C2-AMER AL-DOSSARY", items: new[] { Seed.LeadItem(1, "L1", 1) });
            lead.CustomerCompanyNameExtracted = "SAUDI ELECTRICITY CO.";
            lead.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
            lead.SupplierAccountRefOnDocument = "2004414";
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            SeedAuthoritativeEvidence(seed, 100);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx, aliasLearner: new ERP_RFQ_Automation.CustomerResolution.CustomerAliasLearner(ctx));
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "approve",
            Reason = "Confirmed the client against the bid document.",
            Header = new LeadReviewHeaderDTO { CustomerId = 700 },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var reviewed = verify.Leads.Single(l => l.Id == 100);
        Assert.Equal(700, reviewed.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved, reviewed.CustomerMatchStatus);

        var learned = verify.Set<ERP_RFQ_Automation.CommercialRouting.CustomerIdentifier>()
            .Where(i => i.Source == ERP_RFQ_Automation.CustomerResolution.CustomerIdentifierSources.LeadReviewLearned)
            .ToList();
        Assert.NotEmpty(learned);
        Assert.All(learned, i =>
        {
            Assert.Equal(700, i.CustomerId);
            Assert.Equal(100, i.LearnedFromLeadId);
            // The audit row that carries the before/after image of this correction.
            Assert.NotNull(i.LearnedFromReviewAuditId);
        });
        Assert.Contains(learned, i => i.IdentifierType
            == ERP_RFQ_Automation.CommercialRouting.CustomerIdentifierType.Alias);
        Assert.Contains(learned, i => i.IdentifierType
            == ERP_RFQ_Automation.CommercialRouting.CustomerIdentifierType.PortalAccount);
    }

    [Fact]
    public async Task Saving_a_client_without_approving_teaches_nothing()
    {
        // "save" is a draft. Only an approval is a commitment worth generalising from.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, 700, Bu, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            lead.CustomerCompanyNameExtracted = "SAUDI ELECTRICITY CO.";
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx, aliasLearner: new ERP_RFQ_Automation.CustomerResolution.CustomerAliasLearner(ctx));
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Header = new LeadReviewHeaderDTO { CustomerId = 700 },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        Assert.Equal(700, verify.Leads.Single(l => l.Id == 100).CustomerId);
        Assert.Empty(verify.Set<ERP_RFQ_Automation.CommercialRouting.CustomerIdentifier>()
            .Where(i => i.Source == ERP_RFQ_Automation.CustomerResolution.CustomerIdentifierSources.LeadReviewLearned));
    }

    [Fact]
    public async Task Approve_WithoutAuthoritativeEvidence_IsRejectedWithoutMutation()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        var error = await Assert.ThrowsAsync<LeadReviewValidationException>(() =>
            repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "approve",
                Reason = "Verified without a source.",
                Items = new() { ItemDto(1, "L1", 1) }
            }));

        Assert.Contains("authoritative source-document evidence", error.Message, StringComparison.Ordinal);
        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Include(x => x.EmailIngests).Single(x => x.Id == 100);
        Assert.False(lead.CommercialFactsVerified);
        Assert.Equal("NeedsReview", lead.EmailIngests.ParseStatus);
        Assert.Empty(verify.Set<LeadReviewAudit>());
    }

    [Fact]
    public async Task Save_LeavesLeadStatusNull()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        Assert.Null(verify.Leads.Single(l => l.Id == 100).LeadStatusId);
    }

    [Fact]
    public async Task HeaderFields_OnlyApplied_WhenNonNull()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, buyersName: "Original Buyer", parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Header = new LeadReviewHeaderDTO
            {
                Rfqno = "RFQ-CHANGED", BuyersName = null,
                DeliveryLocation = "North Logistics Hub, Gate 4",
                AgreementReference = "FRAME-2026-118"
            },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Single(l => l.Id == 100);
        Assert.Equal("RFQ-CHANGED", lead.Rfqno);          // provided -> updated
        Assert.Equal("Original Buyer", lead.BuyersName);   // null -> preserved
        Assert.Equal("North Logistics Hub, Gate 4", lead.DeliveryLocation);
        Assert.Equal("FRAME-2026-118", lead.AgreementReference);
    }

    [Fact]
    public async Task Save_PreservesNeedsReviewMarker_WhenNoRemarkSupplied()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, headerRemarks: "[NEEDS REVIEW] please verify quantities", parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Header = new LeadReviewHeaderDTO { HeaderRemarks = null },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        Assert.Equal("[NEEDS REVIEW] please verify quantities", verify.Leads.Single(l => l.Id == 100).HeaderRemarks);
    }

    [Fact]
    public async Task ForeignItemId_IsRejectedWithoutDeletingRealItems()
    {
        // Submitting only an id that does not belong to the lead: the code trusts nothing —
        // the foreign id is skipped (not inserted) and the real item, absent from the payload,
        // is deleted. Documents the "opt-in survival" contract of the upsert.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview", items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await Assert.ThrowsAsync<LeadReviewConflictException>(() => repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "save",
            Items = new() { ItemDto(9999, "ghost", 1) }
        }));

        using var verify = db.ContextFor(Bu);
        Assert.Single(verify.LeadItems.Where(i => i.LeadId == 100));
    }

    [Fact]
    public async Task Save_IncrementsVersionAndWritesImmutableAudit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, buyersName: "Before", parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using (var context = db.ContextFor(Bu))
        {
            var repo = new LeadRepository(context);
            await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Header = new LeadReviewHeaderDTO { BuyersName = "After" },
                Items = new() { ItemDto(1, "L1", 1) }
            }, "reviewer@example.com");
        }

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Include(l => l.EmailIngests).Single(l => l.Id == 100);
        Assert.Equal(2, lead.ReviewVersion);
        Assert.Equal("NeedsReview", lead.EmailIngests.ParseStatus);
        Assert.False(lead.CommercialFactsVerified);
        var audit = verify.Set<LeadReviewAudit>().Single(a => a.LeadId == 100);
        Assert.Equal((1, 2), (audit.FromVersion, audit.ToVersion));
        Assert.Equal("reviewer@example.com", audit.ReviewedBy);
        Assert.Contains("Before", audit.BeforeJson);
        Assert.Contains("After", audit.AfterJson);
    }

    [Fact]
    public async Task StaleVersion_IsRejectedWithoutMutationOrAudit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, buyersName: "Original", parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var context = db.ContextFor(Bu);
        var repo = new LeadRepository(context);
        await Assert.ThrowsAsync<LeadReviewConflictException>(() => repo.SubmitLeadReviewAsync(100, Bu,
            new LeadReviewSubmitDTO
            {
                Action = "save",
                ExpectedVersion = 99,
                Header = new LeadReviewHeaderDTO { BuyersName = "Wrong" },
                Items = new() { ItemDto(1, "L1", 1) }
            }, "reviewer@example.com"));

        using var verify = db.ContextFor(Bu);
        Assert.Equal("Original", verify.Leads.Single(l => l.Id == 100).BuyersName);
        Assert.Empty(verify.Set<LeadReviewAudit>());
    }

    [Fact]
    public async Task MissingVersion_IsRejectedWithoutMutationOrAudit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, buyersName: "Original", parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var context = db.ContextFor(Bu);
        var repo = new LeadRepository(context);
        await Assert.ThrowsAsync<LeadReviewValidationException>(() => repo.SubmitLeadReviewAsync(100, Bu,
            new LeadReviewSubmitDTO
            {
                Action = "save",
                Items = new() { ItemDto(1, "L1", 1) }
            }, "reviewer@example.com"));

        Assert.Equal("Original", context.Leads.Single(l => l.Id == 100).BuyersName);
        Assert.Empty(context.Set<LeadReviewAudit>());
    }

    [Fact]
    public async Task Approve_VerifiesCommercialFactsAndRejectsInvalidValues()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 100, Bu, parseStatus: "NeedsReview",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            lead.RequiresCommercialReview = true;
            SeedAuthoritativeEvidence(seed, 100);
            seed.SaveChanges();
        }

        using (var invalidContext = db.ContextFor(Bu))
        {
            var repo = new LeadRepository(invalidContext);
            await Assert.ThrowsAsync<LeadReviewValidationException>(() => repo.SubmitLeadReviewAsync(100, Bu,
                new LeadReviewSubmitDTO
                {
                    ExpectedVersion = 1,
                    Action = "approve",
                    Reason = "Checked",
                    Items = new() { ItemDto(1, "L1", null) }
                }, "reviewer@example.com"));
        }

        using (var approveContext = db.ContextFor(Bu))
        {
            var repo = new LeadRepository(approveContext);
            await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "approve",
                Reason = "Verified against source document.",
                Items = new() { ItemDto(1, "L1", 1) }
            }, "reviewer@example.com");
        }

        using var verify = db.ContextFor(Bu);
        var approved = verify.Leads.Include(l => l.EmailIngests).Single(l => l.Id == 100);
        // Approval CLEARS the demand it satisfies. Leaving RequiresCommercialReview set was not
        // a stricter posture — the "ready-for-rfq" queue selects on
        // `CommercialFactsVerified && !RequiresCommercialReview`, so an approved lead stayed
        // invisible in the one list whose job is to show approved leads, permanently.
        Assert.False(approved.RequiresCommercialReview);
        Assert.True(approved.CommercialFactsVerified);
        Assert.Equal("reviewer@example.com", approved.ReviewApprovedBy);
        Assert.NotNull(approved.ReviewApprovedOn);
        Assert.Equal("Success", approved.EmailIngests.ParseStatus);
        Assert.Equal("approve", verify.Set<LeadReviewAudit>().Single().Action);
    }

    [Fact]
    public async Task CrossTenantLead_IsNotFound_AndReturnsNull()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 200, businessUnitId: 2, items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        // Repository backed by a BU1-scoped context asking for the BU1 view of lead 200.
        using var ctx = db.ContextFor(1);
        var repo = new LeadRepository(ctx);
        var result = await repo.SubmitLeadReviewAsync(200, 1, new LeadReviewSubmitDTO
        {
            ExpectedVersion = 1,
            Action = "approve",
            Items = new()
        });

        Assert.Null(result);
        using var verify = db.ContextFor(null);
        Assert.NotEqual(24, verify.Leads.Single(l => l.Id == 200).LeadStatusId); // untouched
    }
}
