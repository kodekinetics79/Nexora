using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner audit requirement (ingestion fairness): the lead read models must
/// surface WHEN a lead actually entered Nexora (earliest source-document
/// received_on, CreatedDate fallback), a lead ingested after its business due
/// date must be flagged LateIngested, and the dashboard aging metric must
/// exclude such leads — visibly, via a reported excluded count — so losses that
/// predate Nexora are never booked against Nexora's performance.
/// </summary>
public class LeadIngestionAuditTests
{
    private static readonly DateTime SeedCreatedDate = new(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

    // ── Policy unit tests: the LateIngested boundary ─────────────────────────

    [Fact]
    public void Ingestion_exactly_at_the_due_date_is_not_late()
    {
        var due = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var ingestedAtDue = new DateTimeOffset(due);

        Assert.False(LeadIngestionAudit.IsLateIngested(ingestedAtDue, due, null));
    }

    [Fact]
    public void Ingestion_after_the_due_date_is_late()
    {
        var due = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var oneSecondLate = new DateTimeOffset(due).AddSeconds(1);

        Assert.True(LeadIngestionAudit.IsLateIngested(oneSecondLate, due, null));
    }

    [Fact]
    public void Ingestion_before_the_due_date_is_not_late()
    {
        var due = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        var early = new DateTimeOffset(due).AddDays(-3);

        Assert.False(LeadIngestionAudit.IsLateIngested(early, due, null));
    }

    [Fact]
    public void Sentinel_due_dates_never_flag_a_lead_late()
    {
        var ingested = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

        // DateTime.MinValue and other pre-2000 placeholders mean "no deadline".
        Assert.False(LeadIngestionAudit.IsLateIngested(ingested, DateTime.MinValue, null));
        Assert.False(LeadIngestionAudit.IsLateIngested(ingested, new DateTime(1999, 12, 31), null));
        Assert.False(LeadIngestionAudit.IsLateIngested(ingested, null, null));
    }

    [Fact]
    public void SubDate_is_the_due_date_when_bid_closing_is_missing_or_sentinel()
    {
        var subDate = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        var afterSub = new DateTimeOffset(subDate).AddHours(1);
        var beforeSub = new DateTimeOffset(subDate).AddHours(-1);

        Assert.True(LeadIngestionAudit.IsLateIngested(afterSub, null, subDate));
        Assert.True(LeadIngestionAudit.IsLateIngested(afterSub, DateTime.MinValue, subDate));
        Assert.False(LeadIngestionAudit.IsLateIngested(beforeSub, null, subDate));
    }

    [Fact]
    public void Resolution_prefers_source_received_on_and_falls_back_to_created_date()
    {
        var receivedOn = new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
        var created = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(receivedOn, LeadIngestionAudit.ResolveIngestionTimestamp(receivedOn, created));
        Assert.Equal(new DateTimeOffset(created), LeadIngestionAudit.ResolveIngestionTimestamp(null, created));
    }

    // ── Read-model integration: earliest received_on surfaces on list + detail ──

    [Fact]
    public async Task Lead_list_and_detail_surface_the_earliest_source_received_on()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 900, 90);
        lead.BidClosingDate = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var earliest = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero);
        LinkSourceOccurrence(context, 90, 900, later, "occ-900-later");
        LinkSourceOccurrence(context, 90, 900, earliest, "occ-900-earliest");

        var repo = new LeadRepository(context);
        var (rows, _) = await repo.GetLeadListAsync(1, 10, null, null, null, null, 90);
        var listRow = Assert.Single(rows);
        Assert.Equal(earliest, listRow.IngestedOn);
        Assert.False(listRow.LateIngested); // ingested before the 20 Jul deadline

        var detail = await repo.GetLeadByIdAsync(900, 90);
        Assert.NotNull(detail);
        Assert.Equal(earliest, detail!.IngestedOn);
        Assert.False(detail.LateIngested);
    }

    [Fact]
    public async Task Manual_leads_fall_back_to_created_date_and_flag_late_when_past_due()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        // No occurrence lineage: CreatedDate (14 Jul) is the ingestion timestamp.
        // Due date 01 Jul is long gone by then → late ingested.
        var lateLead = Seed.Lead(context, 901, 91);
        lateLead.BidClosingDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Boundary through the repository: due date exactly equal to the
        // CreatedDate-derived ingestion timestamp is NOT late.
        var boundaryLead = Seed.Lead(context, 902, 91);
        boundaryLead.BidClosingDate = SeedCreatedDate;
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);
        var (rows, _) = await repo.GetLeadListAsync(1, 10, null, null, null, null, 91);
        var byId = rows.ToDictionary(r => r.Id);

        Assert.Equal(new DateTimeOffset(SeedCreatedDate), byId[901].IngestedOn);
        Assert.True(byId[901].LateIngested);

        Assert.Equal(new DateTimeOffset(SeedCreatedDate), byId[902].IngestedOn);
        Assert.False(byId[902].LateIngested);

        var detail = await repo.GetLeadByIdAsync(901, 91);
        Assert.NotNull(detail);
        Assert.True(detail!.LateIngested);
        Assert.Equal(new DateTimeOffset(SeedCreatedDate), detail.IngestedOn);
    }

    [Fact]
    public async Task Ingestion_lineage_lookup_is_tenant_scoped()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var mine = Seed.Lead(context, 910, 92);
        var other = Seed.Lead(context, 920, 93);
        await context.SaveChangesAsync();

        var mineReceivedOn = new DateTimeOffset(2026, 7, 11, 10, 0, 0, TimeSpan.Zero);
        var otherReceivedOn = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        LinkSourceOccurrence(context, 92, 910, mineReceivedOn, "occ-910");
        LinkSourceOccurrence(context, 93, 920, otherReceivedOn, "occ-920");

        // Asking tenant 92 for both leads returns lineage for ITS lead only —
        // the other tenant's (earlier) occurrence never leaks into the answer.
        var map = await LeadIngestionAudit.EarliestSourceReceivedOnAsync(context, 92, new[] { 910L, 920L });
        Assert.Equal(mineReceivedOn, Assert.Single(map).Value);
        Assert.True(map.ContainsKey(910));
        Assert.False(map.ContainsKey(920));
    }

    // ── Dashboard aggregation: aging excludes late leads, visibly ────────────

    [Fact]
    public async Task Team_workload_overdue_aging_excludes_late_ingested_leads_and_reports_the_count()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        const long bu = 95;

        Seed.LeadStatus(context, 24, bu, "Accepted");
        context.Users.Add(new User
        {
            Id = 9501,
            FirstName = "Rep",
            LastName = "One",
            Email = "rep.one@example.test",
            PasswordHash = "x",
            ImageUrl = "n/a",
            Buid = bu,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = SeedCreatedDate
        });

        var dueDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc); // past → overdue by date

        // Ingested BEFORE its due date → fair game for the aging metric.
        var fair = Seed.Lead(context, 950, bu, leadStatusId: 24);
        fair.BidClosingDate = dueDate;
        fair.AssignTo = 9501;

        // Ingested AFTER its due date → excluded from the aging metric.
        var lateAssigned = Seed.Lead(context, 951, bu, leadStatusId: 24);
        lateAssigned.BidClosingDate = dueDate;
        lateAssigned.AssignTo = 9501;

        // Unassigned-bucket variant of the same exclusion.
        var lateUnassigned = Seed.Lead(context, 952, bu, leadStatusId: 24);
        lateUnassigned.BidClosingDate = dueDate;
        await context.SaveChangesAsync();

        LinkSourceOccurrence(context, bu, 950, new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero), "occ-950");
        LinkSourceOccurrence(context, bu, 951, new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero), "occ-951");
        LinkSourceOccurrence(context, bu, 952, new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero), "occ-952");

        var dto = await new DashboardRepository(context).GetTeamWorkloadAsync(bu);

        var repRow = Assert.Single(dto.Rows, r => r.UserId == 9501);
        Assert.Equal(2, repRow.OpenLeads);           // both assigned leads stay visible as workload
        Assert.Equal(1, repRow.OverdueLeads);        // only the fairly-aged lead counts as overdue

        var bucket = Assert.Single(dto.Rows, r => r.IsUnassignedBucket);
        Assert.Equal(1, bucket.OpenLeads);
        Assert.Equal(0, bucket.OverdueLeads);        // late-ingested unassigned lead excluded too

        Assert.Equal(2, dto.LateIngestedExcludedLeads); // exclusion is visible, never silent
    }

    [Fact]
    public async Task Team_workload_counts_normally_ingested_overdue_leads_unchanged()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        const long bu = 96;

        Seed.LeadStatus(context, 24, bu, "Accepted");
        var lead = Seed.Lead(context, 960, bu, leadStatusId: 24);
        lead.BidClosingDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();
        LinkSourceOccurrence(context, bu, 960, new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), "occ-960");

        var dto = await new DashboardRepository(context).GetTeamWorkloadAsync(bu);

        var bucket = Assert.Single(dto.Rows, r => r.IsUnassignedBucket);
        Assert.Equal(1, bucket.OverdueLeads);
        Assert.Equal(0, dto.LateIngestedExcludedLeads);
    }

    // ── Seed helper: corpus → source document → occurrence → lead lineage ────

    private static void LinkSourceOccurrence(
        ErpRfqAutomationContext context, long businessUnitId, long leadId, DateTimeOffset receivedOn, string key)
    {
        var corpus = DocumentCorpus.Create(businessUnitId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        context.SaveChanges();

        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var source = SourceDocument.Create(businessUnitId, corpus.Id, contentHash, $"{key}.pdf",
            "application/pdf", "bucket", $"tenant/{businessUnitId}/{key}", "v1", 64);
        context.Set<SourceDocument>().Add(source);
        context.SaveChanges();

        var occurrence = SourceDocumentOccurrence.Create(
            businessUnitId, source.Id, corpus.Id, key, "{}", receivedOn: receivedOn);
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        context.SaveChanges();

        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(),
            BusinessUnitId = businessUnitId,
            SourceChannel = "ManualUpload",
            CreatedBy = "seed",
            CreatedAtUtc = receivedOn,
            UpdatedAtUtc = receivedOn
        };
        context.Set<LeadIngestionBatch>().Add(batch);
        context.Set<LeadIngestionOccurrence>().Add(new LeadIngestionOccurrence
        {
            BusinessUnitId = businessUnitId,
            BatchId = batch.Id,
            LeadId = leadId,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceChannel = "ManualUpload",
            IdempotencyKey = key,
            LogicalInquiryFingerprint = $"fp-{key}",
            Classification = LeadOccurrenceClassification.New,
            Confidence = 1m,
            ProcessingPath = LeadProcessingPath.Deterministic,
            SourceReceivedAtUtc = receivedOn,
            IngestedAtUtc = receivedOn,
            CreatedAtUtc = receivedOn,
            ActorType = "Service",
            ActorId = "seed",
            CorrelationId = key
        });
        context.SaveChanges();
    }
}
