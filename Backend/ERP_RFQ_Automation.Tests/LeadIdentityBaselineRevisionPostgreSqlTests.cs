using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Canonical identity baseline for leads created by doors that bypass the identity pipeline.
///
/// <para><b>The defect.</b> ManualUploadService, EmailService and LeadUploaderService each did
/// <c>Leads.Add(lead)</c> and never called <see cref="ILeadIdentityApplicationService"/>. Those
/// leads carry line items but no revision, so commercial line resolution throws "The lead has no
/// immutable current revision" and RFQ conversion fails outright. A lead uploaded today was born
/// unconvertible — this was never a legacy-data problem.</para>
///
/// <para>PostgreSQL because the revision/occurrence FKs, the append-only guards and the
/// provenance trigger are all database-enforced and absent on the SQLite lane.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadIdentityBaselineRevisionPostgreSqlTests
{
    private const long Tenant = 950_401;
    private const long CustomerId = 950_411;

    private readonly PostgreSqlTestDatabase _database;

    public LeadIdentityBaselineRevisionPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    private async Task SeedTenantAsync()
    {
        await using var owner = _database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;
        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        owner.Customers.Add(new Customer
        {
            Id = CustomerId, Name = "Baseline Customer", Buid = Tenant, IsActive = true,
            ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        await owner.SaveChangesAsync();
    }

    /// <summary>A lead exactly as the three bypassing doors create it: real lines, no revision.</summary>
    private async Task<long> DoorCreatedLeadAsync(string reference, int lineCount = 3)
    {
        await SeedTenantAsync();
        await using var db = _database.ContextFor(null);
        var lead = new Lead
        {
            Rfqno = reference, BuyersName = "SEC Bid Desk", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(14), LeadSource = "ManualUpload",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Tenant, NoOfLineItems = lineCount
        };
        for (var i = 1; i <= lineCount; i++)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = $"000{i}0", ItemMaterialCode = $"BASE-{i:0000}",
                ManufacturerPartNumber = $"BASE-{i:0000}", ProductShortDescription = $"Line {i}",
                Quantity = i * 4, UnitOfMeasure = "EA", Currency = "SAR"
            });
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private static LeadIdentityBaselineRequest Request(string channel = "ManualUpload") =>
        new(channel, "Test: canonical identity established at creation.", "User", "tests", "test-corr");

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_establishes_revision_one_without_touching_lead_items()
    {
        // THE load-bearing assertion. ApplyCurrentProjection (used by the revision path) deletes
        // and recreates LeadItems, churning ids that canonical_line_items references with no FK.
        // The baseline writer must never go near it.
        var leadId = await DoorCreatedLeadAsync("BASE-A");

        long[] itemIdsBefore;
        await using (var before = _database.ContextFor(null))
            itemIdsBefore = await before.LeadItems.AsNoTracking()
                .Where(i => i.LeadId == leadId).OrderBy(i => i.Id).Select(i => i.Id).ToArrayAsync();

        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act)
                .EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        var itemIdsAfter = await db.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == leadId).OrderBy(i => i.Id).Select(i => i.Id).ToArrayAsync();
        Assert.Equal(itemIdsBefore, itemIdsAfter);   // byte-identical: nothing was recreated

        var lead = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
        Assert.NotNull(lead.CurrentRevisionId);
        Assert.Equal(1, lead.CurrentRevisionNumber);

        var revision = Assert.Single(await db.Set<LeadRevision>().AsNoTracking().Include(r => r.Items)
            .Where(r => r.BusinessUnitId == Tenant && r.LeadId == leadId).ToListAsync());
        Assert.Equal(1, revision.RevisionNumber);
        Assert.True(revision.EstablishedByOccurrenceId > 0);
        Assert.Equal(itemIdsAfter.Length, revision.Items.Count);
        Assert.Equal(Enumerable.Range(1, itemIdsAfter.Length), revision.Items.OrderBy(i => i.LineNumber).Select(i => i.LineNumber));

        // No impacts, no diffs, and above all no second Lead.
        Assert.Empty(await db.Set<LeadRevisionImpact>().AsNoTracking().Where(x => x.LeadId == leadId).ToListAsync());
        Assert.Equal(1, await db.Leads.AsNoTracking().CountAsync(l => l.BusinessUnitId == Tenant && l.Rfqno == "BASE-A"));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_snapshot_rehashes_to_its_own_fingerprint()
    {
        // Pins "no envelope". Fingerprint() hashes exactly the string stored in SnapshotJson, and
        // Diff/IndexedLines read rfq/buyer/closing/items positionally at the top level — wrapping
        // the snapshot to carry provenance would silently corrupt the first real amendment diff.
        var leadId = await DoorCreatedLeadAsync("BASE-B");

        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        var lead = await db.Leads.AsNoTracking().Include(l => l.LeadItems).SingleAsync(l => l.Id == leadId);
        var revision = await db.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(r => r.BusinessUnitId == Tenant && r.LeadId == leadId);

        Assert.Equal(LeadIdentityApplicationService.Fingerprint(lead), revision.LogicalInquiryFingerprint);
        Assert.StartsWith("{", revision.SnapshotJson);
        Assert.Contains("\"rfq\"", revision.SnapshotJson);
        Assert.Contains("\"items\"", revision.SnapshotJson);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_asserts_no_receipt()
    {
        // The honesty assertion. A baseline records CONTENT, not arrival — every document field
        // must be null, or the row claims a document was received when none was.
        var leadId = await DoorCreatedLeadAsync("BASE-C");

        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        var occurrence = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(o => o.BusinessUnitId == Tenant && o.LeadId == leadId);

        Assert.Equal(LeadOccurrenceRecordKind.IdentityBaseline, occurrence.RecordKind);
        Assert.Null(occurrence.ContentHash);
        Assert.Null(occurrence.SourceReceivedAtUtc);
        Assert.Null(occurrence.Sender);
        Assert.Null(occurrence.OriginalFileName);
        Assert.Null(occurrence.MimeType);
        Assert.Null(occurrence.SourceDocumentId);
        Assert.False(occurrence.ExternalAiUsed);
        Assert.Contains("records content, not arrival", occurrence.DecisionReasonsJson);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_is_idempotent_and_writes_nothing_the_second_time()
    {
        var leadId = await DoorCreatedLeadAsync("BASE-D");

        long firstRevisionId;
        await using (var first = _database.ContextFor(null))
            firstRevisionId = (await new LeadIdentityApplicationService(first)
                .EstablishBaselineRevisionAsync(Tenant, leadId, Request())).RevisionId!.Value;

        await using (var second = _database.ContextFor(null))
        {
            var replay = await new LeadIdentityApplicationService(second)
                .EstablishBaselineRevisionAsync(Tenant, leadId, Request());
            Assert.Equal(firstRevisionId, replay.RevisionId);
        }

        await using var db = _database.ContextFor(null);
        Assert.Single(await db.Set<LeadRevision>().AsNoTracking()
            .Where(r => r.BusinessUnitId == Tenant && r.LeadId == leadId).ToListAsync());
        Assert.Single(await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(o => o.BusinessUnitId == Tenant && o.LeadId == leadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_record_kind_is_provenance_immutable()
    {
        // The discriminator every analytics reader now trusts must not be editable after the
        // fact, or the exclusion it drives is worthless.
        var leadId = await DoorCreatedLeadAsync("BASE-E");
        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            $"""UPDATE "LeadIngestionOccurrences" SET "RecordKind" = 'Ingestion' WHERE "LeadId" = {leadId}"""));
        Assert.Contains("immutable", error.InnerException?.Message ?? error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_is_excluded_from_ingestion_analytics()
    {
        // The test that would have caught both fatal objections: a baseline must not be counted
        // as a received document in ingestion volume or leads-received.
        var leadId = await DoorCreatedLeadAsync("BASE-F");
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        var to = DateTimeOffset.UtcNow.AddHours(1);

        LeadIdentityAnalyticsDto before, after;
        await using (var db = _database.ContextFor(null))
            before = await new LeadIdentityApplicationService(db).GetAnalyticsAsync(Tenant, from, to);

        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using (var db = _database.ContextFor(null))
            after = await new LeadIdentityApplicationService(db).GetAnalyticsAsync(Tenant, from, to);

        decimal Metric(LeadIdentityAnalyticsDto d, string key) =>
            d.Metrics.FirstOrDefault(m => m.Key == key)?.Value ?? 0m;

        Assert.Equal(Metric(before, "ingestion-volume"), Metric(after, "ingestion-volume"));
        Assert.Equal(Metric(before, "leads-received"), Metric(after, "leads-received"));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_lead_that_already_has_identity_is_untouched()
    {
        // Safe to call unconditionally from every door — that is what lets the doors call it
        // without first checking, and what makes the backlog sweeper harmless.
        var leadId = await DoorCreatedLeadAsync("BASE-G");
        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        int versionBefore;
        await using (var read = _database.ContextFor(null))
            versionBefore = (await read.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId)).IdentityVersion;

        await using (var again = _database.ContextFor(null))
            await new LeadIdentityApplicationService(again).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        Assert.Equal(versionBefore, (await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId)).IdentityVersion);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_baselined_lead_can_be_commercially_resolved()
    {
        // The acceptance assertion: the gate that refused lead 446 now passes, without the gate
        // itself being weakened by a single character.
        var leadId = await DoorCreatedLeadAsync("BASE-H");

        await using (var act = _database.ContextFor(null))
            await new LeadIdentityApplicationService(act).EstablishBaselineRevisionAsync(Tenant, leadId, Request());

        await using var db = _database.ContextFor(null);
        var lead = await db.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
        Assert.NotNull(lead.CurrentRevisionId);   // the precondition CommercialLineResolution demands
    }
}
