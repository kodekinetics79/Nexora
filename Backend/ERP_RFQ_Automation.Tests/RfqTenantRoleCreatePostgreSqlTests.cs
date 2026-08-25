using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for the two paths that create RFQs, executed the way
/// production executes them: under the nexora_tenant_app role with the RLS command
/// interceptor, on real PostgreSQL (Testcontainers).
///
/// A 403 kept the manual create path unexercised in production for months, so nothing had
/// ever proven that, under the tenant role, the whole chain works: the
/// nexora_rfq_number_seq nextval, the RLS policies on "Leads" / "CommercialCases" /
/// "RFQ" / "Rfqitems", and the TR_Leads_AssignCommercialCase trigger the shell lead
/// relies on. These tests close that gap for both the RfqRepository shell-lead path and
/// the separate LeadConversionIntelligence path (legacy RFQ numbers, Serializable
/// transaction, governed lifecycle transition).
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class RfqTenantRoleCreatePostgreSqlTests
{
    private const long Tenant = 946_001;
    private const long OtherTenant = 946_002;
    private const long CustomerId = 946_011;
    private const int UomId = 946_021;
    private const long CurrencyId = 946_022;
    private static readonly byte[] EvidenceBytes = Encoding.UTF8.GetBytes(
        "RLS-PROMOTION|00010|RLS-UNMATCHED-VALVE|4|EA|USD");
    private static readonly string EvidenceHash = Convert.ToHexString(SHA256.HashData(EvidenceBytes))
        .ToLowerInvariant();
    private const string EvidenceStorageUri = "memory://tenant-role-promotion/inquiry.xlsx";

    private readonly PostgreSqlTestDatabase _database;

    public RfqTenantRoleCreatePostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Manual_create_without_lead_is_retired_under_the_tenant_role()
    {
        await SeedTenantsAsync();
        int leadsBefore;
        int rfqsBefore;
        await using (var before = _database.ContextFor(null))
        {
            leadsBefore = await before.Leads.AsNoTracking().CountAsync(l => l.BusinessUnitId == Tenant);
            rfqsBefore = await before.Rfqs.AsNoTracking().CountAsync(r => r.BusinessUnitId == Tenant);
        }

        await using (var tenantContext = _database.TenantContextWithRls(Tenant))
        {
            var rfq = new Rfq
            {
                Rfqno = string.Empty,
                BuyersName = "Tenant Role Buyer",
                RecDate = DateTime.UtcNow,
                BusinessUnitId = Tenant,
                CustomerId = CustomerId,
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                NoOfLineItems = 1,
                Rfqitems =
                {
                    // Currency-silent line: creation must not invent a currency for it.
                    new Rfqitem
                    {
                        LineItemNo = "1",
                        ProductShortName = "Tenant Widget",
                        Quantity = 2,
                        CreatedBy = "tests",
                        CreatedDate = DateTime.UtcNow
                    }
                }
            };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RfqRepository(tenantContext).AddAsync(rfq));
            Assert.Contains("Direct formal RFQ creation is retired", error.Message, StringComparison.Ordinal);
        }

        await using var owner = _database.ContextFor(null);
        Assert.Equal(rfqsBefore, await owner.Rfqs.AsNoTracking().CountAsync(r => r.BusinessUnitId == Tenant));
        Assert.Equal(leadsBefore, await owner.Leads.AsNoTracking().CountAsync(l => l.BusinessUnitId == Tenant));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Canonical_revision_can_be_governed_and_promoted_end_to_end_under_the_tenant_role()
    {
        await SeedTenantsAsync();
        var reference = $"RLS-PROMOTION-{Guid.NewGuid():N}";
        var intakeKey = $"tenant-role-intake:{Tenant}:{reference}";
        LeadReconciliationResult reconciliation;

        await using (var tenant = _database.TenantContextWithRls(Tenant))
        {
            var candidate = new Lead
            {
                Rfqno = reference,
                BuyersName = "Tenant Role Bid Desk",
                RecDate = DateTime.UtcNow,
                BidClosingDate = DateTime.UtcNow.AddDays(14),
                LeadSource = "TenantRolePromotionTest",
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = Tenant,
                NoOfLineItems = 1
            };
            candidate.LeadItems.Add(new LeadItem
            {
                LineItemNo = "00010",
                ItemMaterialCode = "RLS-UNMATCHED-VALVE",
                ManufacturerPartNumber = "RLS-UNMATCHED-VALVE",
                ProductShortDescription = "Tenant role test valve",
                Quantity = 4,
                UnitOfMeasure = "EA",
                Currency = "USD",
                BidClosingDateLine = DateTime.UtcNow.AddDays(14)
            });
            reconciliation = await new LeadIdentityApplicationService(tenant).ReconcileAsync(candidate,
                new LeadIntakeDescriptor(
                    BatchId: Guid.NewGuid(), SourceChannel: "ManualUpload", IdempotencyKey: intakeKey,
                    ExternalSourceId: intakeKey, EmailThreadId: null, SourceSystem: "TenantRolePromotionTest",
                    Sender: "buyer@example.com", Subject: reference, OriginalFileName: "inquiry.xlsx",
                    MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    FileSize: EvidenceBytes.Length, ContentHash: EvidenceHash, SourceDocumentId: null,
                    ExtractionJobId: null, SourceReceivedAtUtc: DateTimeOffset.UtcNow,
                    IngestedAtUtc: DateTimeOffset.UtcNow, ProcessingPath: LeadProcessingPath.Deterministic,
                    ExternalAiUsed: false, ExternalCost: null, ActorType: "Service", ActorId: "tests",
                    CorrelationId: intakeKey), CancellationToken.None);
        }

        Assert.NotNull(reconciliation.RevisionId);
        var leadId = reconciliation.LeadId;
        var revisionId = reconciliation.RevisionId!.Value;

        await using (var tenant = _database.TenantContextWithRls(Tenant))
        {
            var lead = await tenant.Leads.SingleAsync(x => x.Id == leadId);
            lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
            lead.CommercialFactsVerified = true;
            await tenant.SaveChangesAsync();
        }

        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            await using var tenant = _database.TenantContextWithRls(Tenant);
            var lead = await tenant.Leads.SingleAsync(x => x.Id == leadId);
            await new LifecycleApplicationService(tenant).TransitionLeadAsync(
                Tenant, leadId, new LifecycleActor("tests", "TenantRolePromotionTest"),
                new LifecycleTransitionCommand(target, lead.LifecycleVersion, null, null,
                    "IntegrationTest", $"tenant-role-{leadId}-{target}", $"lead-{leadId}",
                    $"tenant-role-lifecycle:{Tenant}:{leadId}:{target}"), false, CancellationToken.None);
        }

        await EnsurePromotionEvidenceAsync(leadId, revisionId);

        await using (var tenant = _database.TenantContextWithRls(Tenant))
        {
            var lead = await tenant.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
            var revisionLine = await tenant.Set<LeadItemRevision>().AsNoTracking()
                .SingleAsync(x => x.LeadRevisionId == revisionId);
            var participation = new LeadParticipationService(
                tenant, new LeadDecisionService(tenant, new GrossMarginService(tenant)),
                new LeadOutcomeReasons(tenant));
            var fit = await participation.RecordFitAssessmentAsync(Tenant, leadId,
                new RecordLeadFitAssessmentCommand(
                    revisionId, lead.CurrentRevisionNumber, null, "FIT",
                    "A human reviewer confirmed every governed fit criterion for this current revision.",
                    LeadParticipationService.GovernedFitCriterionCodes
                        .Select(code => new LeadFitCriterionCommand(code, "PASS", "Confirmed by tenant-role reviewer."))
                        .ToArray(),
                    $"tenant-role-fit:{Tenant}:{leadId}:{revisionId}", "tests"));
            var decision = await participation.CommitDecisionAsync(Tenant, leadId,
                new CommitLeadParticipationCommand(
                    revisionId, lead.CurrentRevisionNumber, null, true, fit.Id,
                    [new LeadLineParticipationCommand(
                        revisionLine.Id, LeadLineParticipationChoice.Bid,
                        ReasonNotes: "Reviewer acknowledged the unmatched catalog line against the retained source.",
                        Quantity: 4, UnitOfMeasure: "EA", Currency: "USD")],
                    $"tenant-role-participation:{Tenant}:{leadId}:{revisionId}", "tests"));
            var promoted = await new RfqPromotionService(
                    tenant, new MemoryEvidenceStorage(EvidenceStorageUri, EvidenceHash, EvidenceBytes))
                .PromoteAsync(Tenant, leadId, new PromoteLeadToRfqCommand(
                    revisionId, lead.CurrentRevisionNumber, decision.Sequence, decision.Id,
                    $"tenant-role-promotion:{Tenant}:{leadId}:{revisionId}", "tests"));

            Assert.StartsWith($"NXR-RFQ-{Tenant}-", promoted.RfqNumber, StringComparison.Ordinal);
            var rfq = await tenant.Rfqs.AsNoTracking().Include(x => x.Rfqitems)
                .SingleAsync(x => x.Id == promoted.RfqId);
            Assert.Equal(leadId, rfq.LeadId);
            Assert.Equal(revisionId, rfq.SourceLeadRevisionId);
            Assert.Equal(decision.Id, rfq.ParticipationDecisionId);
            Assert.Equal(promoted.PromotionId, rfq.PromotionId);
            Assert.Equal(revisionLine.Id, Assert.Single(rfq.Rfqitems).SourceLeadItemRevisionId);
            Assert.Single(await tenant.Set<LeadFitAssessment>().AsNoTracking()
                .Where(x => x.LeadId == leadId).ToListAsync());
            Assert.Single(await tenant.Set<LeadParticipationDecision>().AsNoTracking()
                .Where(x => x.LeadId == leadId && x.IsCommitted).ToListAsync());
            Assert.Single(await tenant.Set<RfqPromotion>().AsNoTracking()
                .Where(x => x.LeadId == leadId).ToListAsync());

            var converted = await tenant.Leads.AsNoTracking().Include(x => x.LeadStatus)
                .SingleAsync(x => x.Id == leadId);
            Assert.Equal("CONVERTED_TO_RFQ", converted.LeadStatus!.SetupCode);
            var promotedEvent = await tenant.CommercialLifecycleEvents.AsNoTracking()
                .SingleAsync(x => x.AggregateType == "Lead" && x.AggregateId == leadId
                    && x.EventType == "PromotedToRfq");
            Assert.True(await tenant.LifecycleOutboxMessages.AsNoTracking()
                .AnyAsync(x => x.LifecycleEventId == promotedEvent.Id));
        }

        await using var foreignTenant = _database.TenantContextWithRls(OtherTenant);
        Assert.False(await foreignTenant.Leads.AsNoTracking().AnyAsync(x => x.Id == leadId));
        Assert.False(await foreignTenant.Rfqs.AsNoTracking().AnyAsync(x => x.LeadId == leadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Create_with_a_foreign_tenant_lead_is_refused_under_the_tenant_role()
    {
        await SeedTenantsAsync();

        long foreignLeadId;
        await using (var owner = _database.ContextFor(null))
        {
            var foreignLead = new Lead
            {
                BuyersName = "Foreign Buyer",
                RecDate = DateTime.UtcNow,
                LeadSource = "IntegrationTest",
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = OtherTenant
            };
            owner.Leads.Add(foreignLead);
            await owner.SaveChangesAsync();
            foreignLeadId = foreignLead.Id;
        }

        await using var tenantContext = _database.TenantContextWithRls(Tenant);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(tenantContext).AddAsync(new Rfq
            {
                Rfqno = string.Empty,
                RecDate = DateTime.UtcNow,
                BusinessUnitId = Tenant,
                LeadId = foreignLeadId,
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow
            }));

        Assert.Contains("Direct formal RFQ creation is retired", error.Message, StringComparison.Ordinal);
        await using var assertOwner = _database.ContextFor(null);
        Assert.Equal(0, await assertOwner.Rfqs.AsNoTracking().CountAsync(r => r.LeadId == foreignLeadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Intelligence_conversion_is_retired_under_the_tenant_role()
    {
        await SeedTenantsAsync();

        long leadId;
        await using (var owner = _database.ContextFor(null))
        {
            var qualifiedId = await LifecycleStatusCatalog.ResolveIdAsync(owner, Tenant, "Lead", "QUALIFIED");
            var lead = new Lead
            {
                BuyersName = "Conversion Buyer",
                RecDate = DateTime.UtcNow,
                BidClosingDate = DateTime.UtcNow.AddDays(7),
                LeadSource = "IntegrationTest",
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = Tenant,
                LeadStatusId = qualifiedId,
                NoOfLineItems = 1
            };
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = "1",
                ProductShortDescription = "Bearing 6204-2RS",
                Quantity = 4,
                UnitOfMeasure = "EA",
                Currency = "USD"
            });
            owner.Leads.Add(lead);
            await owner.SaveChangesAsync();
            lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
            await owner.SaveChangesAsync();
            leadId = lead.Id;
        }

        await using (var tenantContext = _database.TenantContextWithRls(Tenant))
        {
            // No catalog product matches "Bearing 6204-2RS", so the resolver raises the soft
            // warning "No catalog match found". That used to convert silently; the WP-B1 gate
            // now requires it to be acknowledged with a reason. This test is about the tenant
            // role and RLS, not about the gate, so it acknowledges — and the acknowledgement
            // travelling through the tenant-role path is itself worth proving.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LeadConversionIntelligence(tenantContext)
                .ConvertAsync(leadId, Tenant, new ConvertRequest
                {
                    ActingUser = "tests",
                    AcknowledgeAllWarnings = true,
                    WarningAcknowledgementReason = "Catalog not seeded in this fixture; part verified by the test"
                }, default));
            Assert.Contains("Direct intelligence conversion is retired", error.Message, StringComparison.Ordinal);
        }

        await using var assertOwner = _database.ContextFor(null);
        Assert.Empty(await assertOwner.Rfqs.AsNoTracking().Where(r => r.LeadId == leadId).ToListAsync());
        Assert.Empty(await assertOwner.CommercialLifecycleEvents.AsNoTracking()
            .Where(e => e.AggregateId == leadId && e.EventType == "PromotedToRfq").ToListAsync());
    }

    /// <summary>
    /// Idempotent seed shared by the tests in this class (the collection serializes them,
    /// but their order is not fixed): both business units, the tenant's lifecycle status
    /// catalog, and the customer the shell lead resolves.
    /// </summary>
    private async Task SeedTenantsAsync()
    {
        await using var owner = _database.ContextFor(null);
        var businessUnit = await owner.BusinessUnits.SingleOrDefaultAsync(b => b.Id == Tenant)
            ?? Seed.BusinessUnit(owner, Tenant);
        if (!await owner.BusinessUnits.AnyAsync(b => b.Id == OtherTenant))
            Seed.BusinessUnit(owner, OtherTenant);
        await LifecycleStatusCatalog.EnsureAsync(owner, businessUnit, "tests");
        if (!await owner.Customers.AnyAsync(x => x.Id == CustomerId))
            Seed.Customer(owner, CustomerId, Tenant, "Tenant Role Customer");
        if (!await owner.SetUoms.AnyAsync(x => x.BusinessUnitId == Tenant && x.UomId == UomId))
            owner.SetUoms.Add(new SetUom
            {
                UomId = UomId, BusinessUnitId = Tenant, UomCode = "EA", UomName = "Each",
                IsActive = true, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            });
        if (!await owner.Currencies.AnyAsync(x => x.BusinessUnitId == Tenant && x.Id == CurrencyId))
            owner.Currencies.Add(new Currency
            {
                Id = CurrencyId, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        await owner.SaveChangesAsync();
    }

    private async Task EnsurePromotionEvidenceAsync(long leadId, long revisionId)
    {
        await using var tenant = _database.TenantContextWithRls(Tenant);
        var lead = await tenant.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId);
        var revision = await tenant.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(x => x.Id == revisionId && x.LeadId == leadId);
        var occurrence = await tenant.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(x => x.Id == revision.EstablishedByOccurrenceId);
        var corpus = DocumentCorpus.Create(Tenant, occurrence.BatchId, CorpusSourceType.ManualUpload);
        tenant.Add(corpus);
        await tenant.SaveChangesAsync();

        var job = new ExtractionJob
        {
            BatchId = occurrence.BatchId, BusinessUnitId = Tenant,
            SourceType = ExtractionSourceType.ManualUpload, ContentHash = EvidenceHash,
            StoragePath = EvidenceStorageUri, FileName = "inquiry.xlsx", FileType = "xlsx",
            Status = ExtractionStatus.Succeeded, Priority = 0, SchedulerTag = 0, Attempts = 1,
            MaxAttempts = 5, NextAttemptAt = DateTime.UtcNow, ResultLeadId = leadId,
            CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        };
        tenant.Add(job);
        await tenant.SaveChangesAsync();

        var document = SourceDocument.Create(Tenant, corpus.Id, EvidenceHash, "inquiry.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "memory", "tenant-role-promotion/inquiry.xlsx", EvidenceHash, EvidenceBytes.Length);
        document.ReleaseFromQuarantine("memory", "tenant-role-promotion/inquiry.xlsx", EvidenceHash);
        document.BindExtractionJob(job.Id);
        tenant.Add(document);
        await tenant.SaveChangesAsync();
        tenant.Add(new LeadOccurrenceDocument
        {
            BusinessUnitId = Tenant, OccurrenceId = occurrence.Id, SourceDocumentId = document.Id,
            Role = "Primary", Ordinal = 1, LinkedAtUtc = DateTimeOffset.UtcNow
        });
        var runId = Guid.NewGuid();
        var run = ExtractionRun.Create(Tenant, document.Id, runId, job.Id, 1,
            "native-spreadsheet/tenant-role-test", "lead-evidence/v1");
        var page = DocumentPage.Create(Tenant, document.Id, 1, 100, 100);
        var inquiry = CanonicalInquiry.Create(Tenant, corpus.Id, 1);
        inquiry.PopulateHeader(lead.Rfqno, lead.BuyersName, lead.RecDate, lead.BidClosingDate);
        inquiry.BindLead(leadId);
        tenant.AddRange(run, page, inquiry);
        await tenant.SaveChangesAsync();
        var region = DocumentRegion.Create(Tenant, page.Id, DocumentRegionType.Table,
            0, 0, 100, 100, Encoding.UTF8.GetString(EvidenceBytes), 1m);
        tenant.Add(region);
        await tenant.SaveChangesAsync();

        foreach (var item in lead.LeadItems)
        {
            var canonical = CanonicalLineItem.Create(Tenant, inquiry.Id, 1,
                item.ProductShortDescription ?? item.ItemMaterialCode ?? "Requested line",
                item.Quantity, item.UnitOfMeasure);
            canonical.Enrich(null, item.ManufacturerPartNumber, item.Currency, null, null, "{}",
                CanonicalValidationStatus.Valid);
            canonical.BindLeadItem(item.Id);
            tenant.Add(canonical);
            await tenant.SaveChangesAsync();
            tenant.Add(FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "requestedLine",
                item.ProductShortDescription, item.ManufacturerPartNumber ?? item.ItemMaterialCode,
                1m, "tenant-role-test", runId, validationStatus: FieldValidationStatus.Valid));
        }
        await tenant.SaveChangesAsync();
    }

    private sealed class MemoryEvidenceStorage(string storageUri, string expectedHash, byte[] content)
        : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceObject(storageUri, "memory", storageUri, expectedHash, null, bytes.Length));

        public Task<Stream> OpenVerifiedReadAsync(string requestedUri, string requestedHash,
            CancellationToken ct = default)
        {
            Assert.Equal(storageUri, requestedUri);
            Assert.Equal(expectedHash, requestedHash);
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
