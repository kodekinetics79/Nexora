using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The golden E2E scenario must be established through the real identity pipeline, not built by
/// hand.
///
/// <para><b>The defect this pins.</b> The seeder used to construct a <c>Lead</c> directly. The
/// resulting row had no <c>CurrentRevisionId</c>, because real ingestion creates the canonical
/// Lead, revision 1, the occurrence lineage and the commercial identity together inside
/// <see cref="ILeadIdentityApplicationService.ReconcileAsync"/>. Conversion legitimately refuses
/// a Lead with no immutable current revision, so the hand-built Lead could never convert — the
/// seeder was manufacturing a state the product never produces, and the browser test failed four
/// steps downstream with a message that pointed nowhere near the cause.</para>
///
/// <para>PostgreSQL because revision lineage, the occurrence link and the append-only guards are
/// database-enforced; none of it is exercised on the SQLite lane.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class GoldenJourneyIdentitySeedPostgreSqlTests
{
    private const long Tenant = 949_301;
    private const long CustomerId = 949_311;

    private readonly PostgreSqlTestDatabase _database;

    public GoldenJourneyIdentitySeedPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    private async Task SeedTenantAsync()
    {
        await using var owner = _database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;
        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        owner.Customers.Add(new Customer
        {
            Id = CustomerId, Name = "Identity Seed Customer", Buid = Tenant, IsActive = true,
            ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        await owner.SaveChangesAsync();
    }

    private static Lead SixLineCandidate(string reference)
    {
        var lead = new Lead
        {
            Rfqno = reference, BuyersName = "SEC Bid Desk", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.Date.AddDays(21), LeadSource = "GoldenJourneySeed",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant, NoOfLineItems = 6
        };
        for (var i = 1; i <= 6; i++)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = $"000{i}0",
                ItemMaterialCode = $"GOLD-{i:0000}",
                ManufacturerPartNumber = $"GOLD-{i:0000}",
                ProductShortDescription = $"Golden line {i}",
                Quantity = i == 1 ? 0 : i * 5,     // line 1 is the hard-warning line
                UnitOfMeasure = "EA",
                Currency = "SAR"
            });
        return lead;
    }

    private async Task<LeadReconciliationResult> ReconcileAsync(string reference)
    {
        await SeedTenantAsync();
        await using var db = _database.ContextFor(null);
        var identity = new LeadIdentityApplicationService(db);
        var key = $"golden-journey:{Tenant}:{reference}";
        return await identity.ReconcileAsync(SixLineCandidate(reference), new LeadIntakeDescriptor(
            BatchId: new Guid("00000000-0000-0000-0000-0000000e2e01"),
            SourceChannel: "ManualUpload", IdempotencyKey: key, ExternalSourceId: key,
            EmailThreadId: null, SourceSystem: "GoldenJourneySeed", Sender: "bids@sec.com.sa",
            Subject: $"RFQ {reference}", OriginalFileName: $"{reference}.xlsx",
            MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileSize: 20480, ContentHash: new string('a', 64), SourceDocumentId: null,
            ExtractionJobId: null, SourceReceivedAtUtc: DateTimeOffset.UtcNow,
            IngestedAtUtc: DateTimeOffset.UtcNow, ProcessingPath: LeadProcessingPath.Deterministic,
            ExternalAiUsed: false, ExternalCost: null, ActorType: "Service",
            ActorId: "tests", CorrelationId: key), CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Identity_seeding_creates_revision_one_linked_to_its_occurrence_with_six_lines()
    {
        var result = await ReconcileAsync("E2E-IDENTITY-A");

        Assert.Equal(1, result.RevisionNumber);
        Assert.NotNull(result.RevisionId);
        Assert.True(result.OccurrenceId > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.NexoraSerial));

        await using var db = _database.ContextFor(null);
        var lead = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == result.LeadId);

        // The gate conversion enforces — and the exact field a hand-built Lead lacked.
        Assert.NotNull(lead.CurrentRevisionId);
        Assert.Equal(result.RevisionId, lead.CurrentRevisionId);

        var revisions = await db.Set<LeadRevision>().AsNoTracking().Include(r => r.Items)
            .Where(r => r.BusinessUnitId == Tenant && r.LeadId == result.LeadId).ToListAsync();
        var revision = Assert.Single(revisions);          // exactly one, and it is current
        Assert.Equal(1, revision.RevisionNumber);
        Assert.True(revision.EstablishedByOccurrenceId > 0);
        Assert.Equal(6, revision.Items.Count);
        Assert.Equal(6, await db.LeadItems.AsNoTracking().CountAsync(i => i.LeadId == result.LeadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Replaying_the_same_source_returns_the_same_lead_and_adds_no_second_revision()
    {
        // Idempotency is the identity service's own guarantee, not a seeder trick — the seeder
        // relies on it so a rerun converges instead of duplicating the scenario.
        var first = await ReconcileAsync("E2E-IDENTITY-B");
        var replay = await ReconcileAsync("E2E-IDENTITY-B");

        Assert.Equal(first.LeadId, replay.LeadId);
        Assert.Equal(first.NexoraSerial, replay.NexoraSerial);

        await using var db = _database.ContextFor(null);
        Assert.Equal(1, await db.Leads.AsNoTracking()
            .CountAsync(l => l.BusinessUnitId == Tenant && l.Rfqno == "E2E-IDENTITY-B"));
        Assert.Single(await db.Set<LeadRevision>().AsNoTracking()
            .Where(r => r.BusinessUnitId == Tenant && r.LeadId == first.LeadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_lead_built_outside_the_identity_pipeline_has_no_current_revision()
    {
        // The conversion revision gate is NOT weakened to accommodate seeding. Asserted as the
        // precondition the gate rejects — CurrentRevisionId is null — rather than by re-invoking
        // CommercialLineResolutionApplicationService, whose collaborators are stubbed privately
        // in CommercialLineResolutionApplicationServiceTests; copying those stubs here would
        // duplicate scaffolding without testing anything this file does not already prove.
        await SeedTenantAsync();
        await using var db = _database.ContextFor(null);
        var lead = new Lead
        {
            Rfqno = "E2E-IDENTITY-RAW", BuyersName = "Raw", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(10), LeadSource = "tests",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant, NoOfLineItems = 1
        };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1", ProductShortDescription = "Raw", Quantity = 2,
            UnitOfMeasure = "EA", Currency = "SAR"
        });
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        // Hand-built: no revision, therefore not convertible. This is the exact state the seeder
        // used to produce.
        Assert.Null(lead.CurrentRevisionId);
        Assert.Empty(await db.Set<LeadRevision>().AsNoTracking()
            .Where(r => r.BusinessUnitId == Tenant && r.LeadId == lead.Id).ToListAsync());

        // Identity-created: revision present, therefore convertible.
        var seeded = await ReconcileAsync("E2E-IDENTITY-C");
        var seededLead = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == seeded.LeadId);
        Assert.NotNull(seededLead.CurrentRevisionId);
    }
}
