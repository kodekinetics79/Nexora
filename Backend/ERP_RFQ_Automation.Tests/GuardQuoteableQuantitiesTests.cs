using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The wrong-quantity backstop ("1,000 quoted as 1"), both layers:
///
///  * APPLICATION GATE — RfqRepository.ApproveAsync refuses to build a quote from any
///    RFQ line whose quantity was never established (&lt;= 0), and names the lines. This
///    matters because RfqController.ApproveAsync creates the Quote AND emails it in the
///    same request: nothing between approval and the customer's inbox displays a
///    quantity.
///
///  * DATABASE CONSTRAINTS — CK_RFQItems/QuoteItems/OrderItems_Quantity_Positive are
///    expressed in the MODEL (Models/ErpRfqAutomationContext.cs), not in raw migration
///    SQL, so every database built from the model — including the SQLite TestDb behind
///    these very tests — enforces them, the snapshot records them, and the
///    PostgreSqlProductionDialectTests drift guard can see them. Asserted here via the
///    model so the assertion holds on both the SQLite and PostgreSQL lanes.
///
/// LeadItems is deliberately absent from both layers: it is the raw extraction landing
/// zone where 0 means "the document did not state a quantity" plus a review flag, and a
/// constraint there would force the ingestion doors back into inventing values.
/// </summary>
public sealed class GuardQuoteableQuantitiesTests
{
    private const long Bu = 9_700;
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    // ───────────────────────────────────────────── the approval gate

    [Fact]
    public async Task Rfq_approval_refuses_and_names_the_lines_whose_quantity_was_never_established()
    {
        using var database = new TestDb();
        long rfqId;
        long unnamedLineId;
        await using (var seed = database.ContextFor(null))
        {
            // "Hex bolt M8" has quantity 0 (extraction wrote "never established"), the
            // nameless line is negative, "Stainless washer" is fine and must NOT be named.
            rfqId = SeedApprovableRfq(seed, "RFQ-NOQTY",
                ("Hex bolt M8", 0),
                (null, -3),
                ("Stainless washer", 5));
            unnamedLineId = rfqId + 5; // second seeded line (see SeedApprovableRfq)

            // The rows violate CK_RFQItems_Quantity_Positive, which every database built
            // from the model now carries — including this one. The gate must still hold
            // on a database that PREDATES the constraint (it ships with this release), so
            // the pragma stands in for that legacy state while the bad rows are written.
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = 1;");
            await seed.SaveChangesAsync();
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = 0;");
        }

        await using var db = database.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(db).ApproveAsync(rfqId, "user@example.com", Bu));

        Assert.Contains("no quantity was established", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hex bolt M8", error.Message, StringComparison.Ordinal);
        Assert.Contains($"line {unnamedLineId}", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Stainless washer", error.Message, StringComparison.Ordinal);

        // Fail CLOSED: no quote row exists for QuoteDeliveryWorker to mail.
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Quotes.ToListAsync());
    }

    // ───────────────────────────────────────────── the constraints, asserted via the model

    [Theory]
    [InlineData(typeof(Rfqitem), "CK_RFQItems_Quantity_Positive")]
    [InlineData(typeof(QuoteItem), "CK_QuoteItems_Quantity_Positive")]
    [InlineData(typeof(OrderItem), "CK_OrderItems_Quantity_Positive")]
    public void Quoteable_line_tables_carry_the_positive_quantity_check_in_the_model(
        Type entityType, string constraintName)
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);

        // Check constraints are trimmed from the read-optimized runtime model; the
        // design-time model is the one migrations (and the drift guard) are built from.
        var model = context.GetService<IDesignTimeModel>().Model;
        var entity = model.FindEntityType(entityType)!;
        var constraint = entity.GetCheckConstraints().Single(x => x.Name == constraintName);

        Assert.Equal("\"Quantity\" > 0", constraint.Sql);
    }

    [Fact]
    public void LeadItems_the_extraction_landing_zone_stays_deliberately_unconstrained()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);

        // Pins the panel-endorsed design: LeadItems must be able to represent "the
        // document did not state a quantity" (written as 0 + RequiresCommercialReview).
        // If this test starts failing, someone constrained the landing zone and the
        // ingestion doors are being forced to fabricate quantities again.
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(LeadItem))!;
        Assert.DoesNotContain(entity.GetCheckConstraints(),
            c => c.Sql != null && c.Sql.Contains("Quantity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Databases_built_from_the_model_reject_a_zero_quantity_rfq_line()
    {
        // The panel's objection to the raw-SQL form was precisely that databases built
        // from the model never received the constraint. This proves the TestDb does.
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, Bu);
        db.Rfqs.Add(new Rfq
        {
            Id = 9_800, Rfqno = "RFQ-CK", RecDate = Jan1, BusinessUnitId = Bu,
            CreatedBy = "seed", CreatedDate = Jan1
        });
        db.Rfqitems.Add(new Rfqitem
        {
            Id = 9_801, Rfqid = 9_800, ProductShortName = "Component", Quantity = 0,
            CreatedBy = "seed", CreatedDate = Jan1
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ───────────────────────────────────────────── helpers

    /// <summary>An RFQ in QUOTE_PREPARATION with a resolved commercial identity, ready to
    /// approve. Lines carry no currency (a legal state: the header stays NULL), so the
    /// currency gate upstream of the quantity gate stays out of the way. Line ids are
    /// rfqId + 4, rfqId + 5, … in the order given.</summary>
    private static long SeedApprovableRfq(ErpRfqAutomationContext db, string rfqNo,
        params (string? ProductShortName, int Quantity)[] lines)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        const long offset = 9_500;

        db.SetupMasters.AddRange(
            LifecycleStatus(offset + 1, "RFQStatus", "QUOTE_PREPARATION"),
            LifecycleStatus(offset + 2, "QuoteStatus", "DRAFT"));
        var customer = Seed.Customer(db, offset + 3, Bu, $"Customer {rfqNo}");
        var contact = Seed.Contact(db, offset + 4, Bu, customer.Id);
        var lead = Seed.Lead(db, offset + 5, Bu);
        db.SaveChanges();
        lead.ResolveCommercialIdentity(customer.Id, contact.Id, "CONFIRMED");
        db.SaveChanges();

        var rfq = new Rfq
        {
            Id = offset + 6,
            Rfqno = rfqNo,
            RecDate = Jan1,
            LeadId = lead.Id,
            BusinessUnitId = Bu,
            RfqstatusId = offset + 1,
            CreatedBy = "seed",
            CreatedDate = Jan1
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);

        var lineId = rfq.Id + 4;
        foreach (var (name, quantity) in lines)
            db.Rfqitems.Add(new Rfqitem
            {
                Id = lineId++,
                Rfqid = rfq.Id,
                ProductShortName = name,
                Quantity = quantity,
                UnitPrice = 10m,
                CreatedBy = "seed",
                CreatedDate = Jan1
            });

        return rfq.Id;
    }

    private static SetupMaster LifecycleStatus(long id, string type, string code) => new()
    {
        SetupId = id,
        BusinessUnitId = Bu,
        SetupType = type,
        SetupCode = code,
        SetupValue = code,
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = Jan1
    };
}
