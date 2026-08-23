using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
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

    private readonly PostgreSqlTestDatabase _database;

    public RfqTenantRoleCreatePostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Manual_create_without_lead_works_end_to_end_under_the_tenant_role()
    {
        await SeedTenantsAsync();

        long rfqId;
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

            await new RfqRepository(tenantContext).AddAsync(rfq);
            rfqId = rfq.Id;

            // Server-generated number came from public.nexora_rfq_number_seq under the
            // tenant role (GRANT USAGE), not from anything the client supplied.
            Assert.Matches($@"^NXR-RFQ-{Tenant}-\d{{4}}-\d{{8}}$", rfq.Rfqno);
        }

        await using var owner = _database.ContextFor(null);
        var persistedRfq = await owner.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfqId);
        Assert.NotNull(persistedRfq.LeadId);

        var lead = await owner.Leads.AsNoTracking().SingleAsync(l => l.Id == persistedRfq.LeadId!.Value);
        Assert.Equal(Tenant, lead.BusinessUnitId);
        Assert.Equal("manual-rfq", lead.LeadSource);
        Assert.Equal(CustomerId, lead.CustomerId);
        Assert.Equal("CUSTOMER_CONFIRMED", lead.CustomerMatchStatus);

        // The database trigger allocated a commercial case and the RFQ inherited it —
        // the serial lineage holds end-to-end.
        Assert.True(lead.CommercialCaseId > 0);
        Assert.False(string.IsNullOrWhiteSpace(lead.CommercialCaseReference));
        Assert.Equal(lead.CommercialCaseId, persistedRfq.CommercialCaseId);
        Assert.Equal(lead.CommercialCaseReference, persistedRfq.NexoraSerial);
        Assert.NotNull(await owner.CommercialCases.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == lead.CommercialCaseId));

        // Born converted, with the trigger-recorded "Created" history row.
        var convertedStatusId = await LifecycleStatusCatalog.ResolveIdAsync(owner, Tenant, "Lead", "CONVERTED_TO_RFQ");
        Assert.Equal(convertedStatusId, lead.LeadStatusId);
        Assert.Single(await owner.LeadStatusHistories.AsNoTracking()
            .Where(h => h.LeadId == lead.Id && h.EventType == "Created").ToListAsync());

        var items = await owner.Rfqitems.AsNoTracking().Where(i => i.Rfqid == rfqId).ToListAsync();
        Assert.Single(items);
        Assert.Null(items[0].CurrencyId); // currency-silent stays currency-silent

        // Row-level security: another tenant sees none of what was just created.
        await using var foreign = _database.TenantContextWithRls(OtherTenant);
        Assert.Equal(0, await foreign.Rfqs.IgnoreQueryFilters().CountAsync(r => r.Id == rfqId));
        Assert.Equal(0, await foreign.Leads.IgnoreQueryFilters().CountAsync(l => l.Id == lead.Id));
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
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new RfqRepository(tenantContext).AddAsync(new Rfq
            {
                Rfqno = string.Empty,
                RecDate = DateTime.UtcNow,
                BusinessUnitId = Tenant,
                LeadId = foreignLeadId,
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow
            }));

        Assert.Contains("does not exist in this business unit", error.Message);
        await using var assertOwner = _database.ContextFor(null);
        Assert.Equal(0, await assertOwner.Rfqs.AsNoTracking().CountAsync(r => r.LeadId == foreignLeadId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Intelligence_conversion_creates_the_rfq_and_lifecycle_event_under_the_tenant_role()
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

        long rfqId;
        await using (var tenantContext = _database.TenantContextWithRls(Tenant))
        {
            // No catalog product matches "Bearing 6204-2RS", so the resolver raises the soft
            // warning "No catalog match found". That used to convert silently; the WP-B1 gate
            // now requires it to be acknowledged with a reason. This test is about the tenant
            // role and RLS, not about the gate, so it acknowledges — and the acknowledgement
            // travelling through the tenant-role path is itself worth proving.
            rfqId = await new LeadConversionIntelligence(tenantContext)
                .ConvertAsync(leadId, Tenant, new ConvertRequest
                {
                    ActingUser = "tests",
                    AcknowledgeAllWarnings = true,
                    WarningAcknowledgementReason = "Catalog not seeded in this fixture; part verified by the test"
                }, default);
        }

        await using var assertOwner = _database.ContextFor(null);
        var rfq = await assertOwner.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfqId);
        var lead2 = await assertOwner.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);

        Assert.Equal(leadId, rfq.LeadId);
        Assert.Equal(lead2.CommercialCaseId, rfq.CommercialCaseId);
        Assert.Equal(lead2.CommercialCaseReference, rfq.NexoraSerial);
        Assert.Equal(CustomerId, rfq.CustomerId);

        var convertedId = await LifecycleStatusCatalog.ResolveIdAsync(assertOwner, Tenant, "Lead", "CONVERTED_TO_RFQ");
        Assert.Equal(convertedId, lead2.LeadStatusId);
        // 1 (created) -> 2 (transition) -> 3 (PromotedToRfq event appended in the same
        // transaction; every appended lifecycle event advances the aggregate version).
        Assert.Equal(3, lead2.LifecycleVersion);
        Assert.Single(await assertOwner.CommercialLifecycleEvents.AsNoTracking()
            .Where(e => e.AggregateType == "Lead" && e.AggregateId == leadId
                        && e.NewStatusCode == "CONVERTED_TO_RFQ" && e.EventType == "StatusTransitioned")
            .ToListAsync());
        // The dedicated promotion event and its outbox message, written under the tenant role.
        var promotion = await assertOwner.CommercialLifecycleEvents.AsNoTracking()
            .SingleAsync(e => e.AggregateType == "Lead" && e.AggregateId == leadId && e.EventType == "PromotedToRfq");
        var outbox = await assertOwner.LifecycleOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.LifecycleEventId == promotion.Id);
        Assert.Equal("commercial-case.lead.promoted-to-rfq", outbox.EventType);
        // Parsed, not substring-matched: the jsonb column re-serialises with its own spacing.
        using var payload = System.Text.Json.JsonDocument.Parse(outbox.Payload);
        Assert.Equal(rfqId, payload.RootElement.GetProperty("RfqId").GetInt64());
        Assert.Equal(leadId, payload.RootElement.GetProperty("LeadId").GetInt64());
    }

    /// <summary>
    /// Idempotent seed shared by the tests in this class (the collection serializes them,
    /// but their order is not fixed): both business units, the tenant's lifecycle status
    /// catalog, and the customer the shell lead resolves.
    /// </summary>
    private async Task SeedTenantsAsync()
    {
        await using var owner = _database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(b => b.Id == Tenant)) return;

        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        Seed.BusinessUnit(owner, OtherTenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        Seed.Customer(owner, CustomerId, Tenant, "Tenant Role Customer");
        await owner.SaveChangesAsync();
    }
}
