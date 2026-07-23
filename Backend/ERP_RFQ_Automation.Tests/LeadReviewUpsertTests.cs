using ERP_RFQ_Automation.DTOs.Lead;
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

    [Fact]
    public async Task ExistingItem_IsUpdatedInPlace()
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
        var item1 = verify.LeadItems.Single(i => i.Id == 1);
        Assert.Equal("Updated Name", item1.ProductShortName);
        Assert.Equal(99, item1.Quantity);
        Assert.Equal(2, verify.LeadItems.Count(i => i.LeadId == 100));
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
    public async Task OmittedItem_IsDeleted()
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
        var ids = verify.LeadItems.Where(i => i.LeadId == 100).Select(i => i.Id).ToList();
        Assert.Equal(new long[] { 1, 2 }, ids.OrderBy(x => x));
        Assert.DoesNotContain(3L, ids);
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
            Header = new LeadReviewHeaderDTO { Rfqno = "RFQ-CHANGED", BuyersName = null },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Single(l => l.Id == 100);
        Assert.Equal("RFQ-CHANGED", lead.Rfqno);          // provided -> updated
        Assert.Equal("Original Buyer", lead.BuyersName);   // null -> preserved
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
        Assert.True(approved.RequiresCommercialReview);
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
