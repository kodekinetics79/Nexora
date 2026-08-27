using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pins the direct-id boundary on processing evidence. Tenant RLS is necessary but insufficient:
/// two sales reps are in the same tenant, and knowing the other rep's id must not make their source
/// documents, extraction attempts, provider costs or customer references readable.
/// </summary>
public sealed class ProcessingEvidenceScopeTests
{
    private const long Bu = 97_100;
    private const long OtherBu = 97_900;
    private const long RepA = 97_101;
    private const long RepB = 97_102;
    private const long DescendantRep = 97_103;
    private const long Manager = 97_104;
    private const long Admin = 97_105;
    private const long OtherTenantRep = 97_901;

    private const long LeadA = 97_111;
    private const long LeadB = 97_112;
    private const long LeadDescendant = 97_113;
    private const long OtherTenantLead = 97_911;
    private static readonly DateTime Now = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Rep_direct_ids_return_not_found_for_another_reps_evidence()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        await SeedAsync(db);
        var controller = Controller(db, Actor(RepA, Assigned(RepA)));

        Assert.IsType<OkObjectResult>((await controller.Lead(LeadA, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Lead(LeadB, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.LeadFields(LeadA, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.LeadFields(LeadB, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.Rfq(RfqId(LeadA), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Rfq(RfqId(LeadB), default)).Result);
        Assert.IsType<OkObjectResult>((await controller.SupplierQuote(SupplierQuoteId(LeadA), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.SupplierQuote(SupplierQuoteId(LeadB), default)).Result);
        Assert.IsType<OkObjectResult>((await controller.ClientPurchaseOrder(PurchaseOrderId(LeadA), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.ClientPurchaseOrder(PurchaseOrderId(LeadB), default)).Result);
    }

    [Fact]
    public async Task Manager_reaches_descendants_but_not_a_non_descendant_reps_evidence()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        await SeedAsync(db);
        var scope = new AccountTeamScope(
            AccountScopeTier.ManagedScope, Manager, [], [Manager, RepA, DescendantRep]);
        var controller = Controller(db, Actor(Manager, scope));

        Assert.IsType<OkObjectResult>((await controller.Lead(LeadA, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.Lead(LeadDescendant, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Lead(LeadB, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.SupplierQuote(SupplierQuoteId(LeadDescendant), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.ClientPurchaseOrder(PurchaseOrderId(LeadB), default)).Result);
    }

    [Fact]
    public async Task Tenant_admin_reaches_all_tenant_evidence_but_not_cross_tenant_ids()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        await SeedAsync(db);
        var controller = Controller(db, Actor(Admin, AccountTeamScope.TenantWide(Admin)));

        Assert.IsType<OkObjectResult>((await controller.Lead(LeadA, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.Lead(LeadB, default)).Result);
        Assert.IsType<OkObjectResult>((await controller.Rfq(RfqId(LeadDescendant), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Lead(OtherTenantLead, default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Rfq(RfqId(OtherTenantLead), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.SupplierQuote(SupplierQuoteId(OtherTenantLead), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.ClientPurchaseOrder(PurchaseOrderId(OtherTenantLead), default)).Result);
    }

    private static ProcessingEvidenceController Controller(
        ErpRfqAutomationContext db, CommercialActorScope actor) =>
        new(db, new FixedCommercialAccessContext(actor));

    private static CommercialActorScope Actor(long userId, AccountTeamScope scope) =>
        new(Bu, userId, RoleId: 1, scope);

    private static AccountTeamScope Assigned(long userId) =>
        new(AccountScopeTier.AssignedAccounts, userId, [], [userId]);

    private static long RfqId(long leadId) => leadId + 1_000;
    private static long SupplierQuoteId(long leadId) => leadId + 2_000;
    private static long PurchaseOrderId(long leadId) => leadId + 3_000;

    private static async Task SeedAsync(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        Seed.EnsureBusinessUnit(db, OtherBu);
        db.Users.AddRange(
            User(RepA, Bu), User(RepB, Bu), User(DescendantRep, Bu),
            User(Manager, Bu), User(Admin, Bu), User(OtherTenantRep, OtherBu));

        var leadA = Lead(db, LeadA, Bu, RepA);
        var leadB = Lead(db, LeadB, Bu, RepB);
        var descendant = Lead(db, LeadDescendant, Bu, DescendantRep);
        var otherTenant = Lead(db, OtherTenantLead, OtherBu, OtherTenantRep);
        await db.SaveChangesAsync();

        AddRfq(db, leadA);
        AddRfq(db, leadB);
        AddRfq(db, descendant);
        AddRfq(db, otherTenant);
        await db.SaveChangesAsync();

        // These tests exercise evidence authorization, not the supplier/customer master-data
        // fixture. Keep the real commercial foreign keys on the rows, while disabling SQLite's
        // fixture-only FK enforcement for the unrelated supplier, solicitation, customer and
        // currency parents. Production PostgreSQL constraints and RLS remain untouched.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        AddDownstreamDocuments(db, leadA);
        AddDownstreamDocuments(db, leadB);
        AddDownstreamDocuments(db, descendant);
        AddDownstreamDocuments(db, otherTenant);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
    }

    private static Lead Lead(ErpRfqAutomationContext db, long id, long businessUnitId, long ownerId)
    {
        var lead = Seed.Lead(db, id, businessUnitId);
        lead.AssignTo = ownerId;
        var commercialCase = new CommercialCase
        {
            Id = id + 10_000,
            BusinessUnitId = businessUnitId,
            CreatedOn = Now,
            CreatedBy = "scope-test"
        };
        commercialCase.AssignIdentity(id, $"NX-{id}");
        lead.AssignCommercialCase(commercialCase);
        db.CommercialCases.Add(commercialCase);
        return lead;
    }

    private static User User(long id, long businessUnitId) => new()
    {
        Id = id,
        FirstName = "Evidence",
        LastName = $"Actor {id}",
        Email = $"evidence-{id}@example.test",
        PasswordHash = "x",
        ImageUrl = "n/a",
        Buid = businessUnitId,
        IsActive = true,
        CreatedBy = "scope-test",
        CreatedOn = Now
    };

    private static void AddRfq(ErpRfqAutomationContext db, Lead lead)
    {
        var rfq = new Rfq
        {
            Id = RfqId(lead.Id),
            Rfqno = $"RFQ-EVIDENCE-{lead.Id}",
            RecDate = Now,
            LeadId = lead.Id,
            BusinessUnitId = lead.BusinessUnitId,
            CreatedBy = "scope-test",
            CreatedDate = Now
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);
    }

    private static void AddDownstreamDocuments(ErpRfqAutomationContext db, Lead lead)
    {
        db.SupplierQuotes.Add(new SupplierQuote
        {
            Id = SupplierQuoteId(lead.Id),
            BusinessUnitId = lead.BusinessUnitId,
            SupplierId = lead.Id + 40_000,
            SupplierSolicitationId = lead.Id + 50_000,
            SourcingCaseId = lead.Id + 60_000,
            RfqId = RfqId(lead.Id),
            NexoraSerial = lead.CommercialCaseReference,
            SupplierQuoteReference = $"SQ-{lead.Id}",
            CurrentRevisionNumber = 1,
            CreatedOn = Now,
            CreatedBy = "scope-test",
            UpdatedOn = Now,
            UpdatedBy = "scope-test"
        });
        db.CustomerPurchaseOrders.Add(new CustomerPurchaseOrder
        {
            Id = PurchaseOrderId(lead.Id),
            BusinessUnitId = lead.BusinessUnitId,
            CommercialCaseId = lead.CommercialCaseId,
            CustomerId = lead.Id + 70_000,
            InternalNumber = $"PO-{lead.Id}",
            ExternalPoNumber = $"CUSTOMER-{lead.Id}",
            NormalizedExternalPoNumber = $"CUSTOMER{lead.Id}",
            PoDate = Now,
            ReceivedOn = Now,
            CurrencyId = lead.Id + 80_000,
            CreatedOn = Now,
            CreatedBy = "scope-test",
            RfqId = RfqId(lead.Id)
        });
    }

    private sealed class FixedCommercialAccessContext(CommercialActorScope actor)
        : ICommercialAccessContext
    {
        public Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default) =>
            Task.FromResult<CommercialActorScope?>(actor);

        public Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
