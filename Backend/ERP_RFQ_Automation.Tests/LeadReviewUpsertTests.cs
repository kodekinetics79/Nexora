using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// LeadRepository.SubmitLeadReviewAsync is the persistence heart of the review workbench:
/// it loads the lead aggregate TRACKED and flushes header edits, item update/insert/delete
/// and the NeedsReview flag clear in a single SaveChanges. These tests exercise the real
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
            Seed.Lead(seed, 100, Bu, items: new[] { Seed.LeadItem(1, "L1", 2) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
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
    }

    [Fact]
    public async Task OmittedItem_IsDeleted()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, items: new[]
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
            Seed.Lead(seed, 100, Bu, items: new[]
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
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1), ItemDto(null, "N1", 1), ItemDto(null, "N2", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Single(l => l.Id == 100);
        Assert.Equal(3, lead.NoOfLineItems);
        Assert.Equal(3, verify.LeadItems.Count(i => i.LeadId == 100));
    }

    [Fact]
    public async Task ParseStatus_FlipsFromNeedsReviewToSuccess()
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
            Action = "save",
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        var lead = verify.Leads.Include(l => l.EmailIngests).Single(l => l.Id == 100);
        Assert.Equal("Success", lead.EmailIngests.ParseStatus);
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
            Action = "approve",
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
            Seed.Lead(seed, 100, Bu, buyersName: "Original Buyer", items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
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
    public async Task HeaderRemarks_NeedsReviewMarker_IsStripped_WhenNoRemarkSupplied()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, headerRemarks: "[NEEDS REVIEW] please verify quantities",
                items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            Action = "save",
            Header = new LeadReviewHeaderDTO { HeaderRemarks = null },
            Items = new() { ItemDto(1, "L1", 1) }
        });

        using var verify = db.ContextFor(Bu);
        Assert.Equal("please verify quantities", verify.Leads.Single(l => l.Id == 100).HeaderRemarks);
    }

    [Fact]
    public async Task ForeignItemId_IsIgnored_AndUnreferencedRealItemsAreRemoved()
    {
        // Submitting only an id that does not belong to the lead: the code trusts nothing —
        // the foreign id is skipped (not inserted) and the real item, absent from the payload,
        // is deleted. Documents the "opt-in survival" contract of the upsert.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, 100, Bu, items: new[] { Seed.LeadItem(1, "L1", 1) });
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var repo = new LeadRepository(ctx);
        await repo.SubmitLeadReviewAsync(100, Bu, new LeadReviewSubmitDTO
        {
            Action = "save",
            Items = new() { ItemDto(9999, "ghost", 1) }
        });

        using var verify = db.ContextFor(Bu);
        Assert.Empty(verify.LeadItems.Where(i => i.LeadId == 100));
        Assert.Equal(0, verify.Leads.Single(l => l.Id == 100).NoOfLineItems);
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
            Action = "approve",
            Items = new()
        });

        Assert.Null(result);
        using var verify = db.ContextFor(null);
        Assert.NotEqual(24, verify.Leads.Single(l => l.Id == 200).LeadStatusId); // untouched
    }
}
