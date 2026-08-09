using ERP_RFQ_Automation.CommercialCases;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The commercial case is the identity allocated at lead ingestion and the one thing that has to
/// survive to the last document of the cycle, won or lost. Adding a <c>CommercialCaseId</c> column
/// to more tables does nothing for traceability until the READER depends on it, and until this
/// suite existed it did not: the timeline was rebuilt by walking foreign keys, so a document with
/// the wrong case still appeared, a document with the right case behind a broken join was invisible,
/// and a document with no case at all was silently absent rather than reported.
///
/// <para>Each test below fails against the foreign-key reader and passes against the declared-key
/// one. That is the whole point of the file — it is the proof the column is load-bearing.</para>
/// </summary>
public sealed class CommercialCaseReadKeyTests
{
    private const long Tenant = 97_401;
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The defect in its purest form. The order is joined to this case's lead by <c>LeadID</c>, so
    /// the foreign-key walk reaches it and the old reader listed it — while the order itself says
    /// it belongs to a different case. Membership follows the declaration, and the contradiction is
    /// reported rather than resolved in the record's favour.
    /// </summary>
    [Fact]
    public async Task A_document_declaring_another_case_is_not_in_this_timeline_and_is_reported()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
        {
            var order = NewOrder(seed, 97_411, "SO-WRONG-CASE", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(order, seed.OtherCaseId, seed.OtherSerial);
            seed.Context.Orders.Add(order);
        });

        var detail = await ReadAsync(db, graph.CaseId);

        Assert.DoesNotContain(detail.Documents, d => d.DocumentId == 97_411 && d.DocumentType == "Order");
        var gap = Assert.Single(detail.TraceabilityGaps, g => g.DocumentId == 97_411 && g.DocumentType == "Order");
        Assert.Equal(CommercialCaseGapKinds.ConflictingCase, gap.GapKind);
        Assert.Equal(graph.OtherCaseId, gap.DeclaredCommercialCaseId);
    }

    /// <summary>
    /// The mirror image. The order declares this case but carries no lead, RFQ or quote link at
    /// all, so the foreign-key walk cannot reach it and the old reader dropped it — a priced
    /// customer document missing from its own case timeline. It is listed, flagged
    /// <see cref="CommercialCaseLinkStates.ChainBroken"/>, and the broken join is reported.
    /// </summary>
    [Fact]
    public async Task A_document_declaring_this_case_appears_even_when_the_document_chain_is_broken()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
        {
            var order = NewOrder(seed, 97_412, "SO-BROKEN-CHAIN", leadId: null);
            Seed.StampCommercialCase(order, seed.CaseId, seed.Serial);
            seed.Context.Orders.Add(order);
        });

        var detail = await ReadAsync(db, graph.CaseId);

        var document = Assert.Single(detail.Documents, d => d.DocumentId == 97_412 && d.DocumentType == "Order");
        Assert.Equal(CommercialCaseLinkStates.ChainBroken, document.LinkState);
        var gap = Assert.Single(detail.TraceabilityGaps, g => g.DocumentId == 97_412);
        Assert.Equal(CommercialCaseGapKinds.ChainBroken, gap.GapKind);
        Assert.Equal(graph.CaseId, gap.DeclaredCommercialCaseId);
    }

    /// <summary>
    /// The honesty requirement. A joinable order with a NULL case is not folded in through the
    /// chain — that would be the old bug wearing a new name — and it is not silently omitted
    /// either. It is named as an unlinked document so the empty column is a visible defect.
    /// </summary>
    [Fact]
    public async Task A_joinable_document_with_no_case_is_surfaced_as_an_unlinked_gap()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
            seed.Context.Orders.Add(NewOrder(seed, 97_413, "SO-NO-CASE", leadId: seed.PrimaryLeadId)));

        var detail = await ReadAsync(db, graph.CaseId);

        Assert.DoesNotContain(detail.Documents, d => d.DocumentId == 97_413 && d.DocumentType == "Order");
        var gap = Assert.Single(detail.TraceabilityGaps, g => g.DocumentId == 97_413);
        Assert.Equal(CommercialCaseGapKinds.Unlinked, gap.GapKind);
        Assert.Null(gap.DeclaredCommercialCaseId);
    }

    /// <summary>
    /// A shipment declaring this case survives even when the sales order it hangs off has been
    /// stamped with a different one — the delivery note keeps naming its own case rather than
    /// inheriting whatever the join happens to find.
    /// </summary>
    [Fact]
    public async Task A_shipment_is_placed_by_its_own_declaration_not_by_its_orders()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
        {
            var order = NewOrder(seed, 97_414, "SO-DIVERGENT", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(order, seed.OtherCaseId, seed.OtherSerial);
            seed.Context.Orders.Add(order);
            var shipment = new Shipment
            {
                Id = 97_415, ShipmentNo = "SH-DIVERGENT", OrderId = 97_414, BusinessUnitId = Tenant,
                StatusId = seed.ShipmentStatusId, ShipmentDate = Now, CreatedBy = "qa", CreatedOn = Now,
                IsActive = true, CommercialCaseId = seed.CaseId, NexoraSerial = seed.Serial
            };
            seed.Context.Shipments.Add(shipment);
        });

        var detail = await ReadAsync(db, graph.CaseId);

        Assert.Single(detail.Documents, d => d.DocumentId == 97_415 && d.DocumentType == "Shipment");
        Assert.DoesNotContain(detail.Documents, d => d.DocumentId == 97_414 && d.DocumentType == "Order");
    }

    /// <summary>
    /// A document that declares this case AND is reachable through the chain is the ordinary,
    /// healthy shape: it is listed, marked reconciled, and produces no gap. Without this the suite
    /// could pass by reporting everything as broken.
    /// </summary>
    [Fact]
    public async Task A_correctly_linked_document_is_reconciled_and_produces_no_gap()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
        {
            var order = NewOrder(seed, 97_416, "SO-HEALTHY", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(order, seed.CaseId, seed.Serial);
            seed.Context.Orders.Add(order);
        });

        var detail = await ReadAsync(db, graph.CaseId);

        var document = Assert.Single(detail.Documents, d => d.DocumentId == 97_416 && d.DocumentType == "Order");
        Assert.Equal(CommercialCaseLinkStates.Reconciled, document.LinkState);
        Assert.DoesNotContain(detail.TraceabilityGaps, g => g.DocumentId == 97_416);
    }

    /// <summary>
    /// The card and the timeline must count the same population. A search result that says "2
    /// orders" over a timeline showing one is the same lie in a smaller font.
    /// </summary>
    [Fact]
    public async Task Search_counts_only_the_orders_that_declare_the_case()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db, seed =>
        {
            var mine = NewOrder(seed, 97_417, "SO-COUNTED", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(mine, seed.CaseId, seed.Serial);
            var theirs = NewOrder(seed, 97_418, "SO-NOT-COUNTED", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(theirs, seed.OtherCaseId, seed.OtherSerial);
            var unlinked = NewOrder(seed, 97_419, "SO-UNCOUNTED", leadId: seed.PrimaryLeadId);
            seed.Context.Orders.AddRange(mine, theirs, unlinked);
        });

        await using var context = db.ContextFor(Tenant);
        var service = new CommercialCaseQueryService(context, new StubTenant(Tenant));

        var results = await service.SearchAsync(Tenant, "SO-COUNTED", 20, default);

        var result = Assert.Single(results);
        Assert.Equal(graph.CaseId, result.Id);
        Assert.Equal(1, result.OrderCount);
    }

    /// <summary>
    /// Typing the number of a document stamped with another case must not surface this case. The
    /// old search reached orders through the lead join, so it did.
    /// </summary>
    [Fact]
    public async Task Search_does_not_surface_a_case_through_a_document_stamped_with_another_case()
    {
        using var db = new TestDb();
        await SeedAsync(db, seed =>
        {
            var theirs = NewOrder(seed, 97_420, "SO-FOREIGN", leadId: seed.PrimaryLeadId);
            Seed.StampCommercialCase(theirs, seed.OtherCaseId, seed.OtherSerial);
            seed.Context.Orders.Add(theirs);
        });

        await using var context = db.ContextFor(Tenant);
        var service = new CommercialCaseQueryService(context, new StubTenant(Tenant));

        var results = await service.SearchAsync(Tenant, "SO-FOREIGN", 20, default);

        // The other case genuinely owns it, so exactly one case matches — never both.
        var result = Assert.Single(results);
        Assert.Equal("NXR-2026-000002", result.MasterReference);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private sealed class SeedContext
    {
        public required ErpRfqAutomationContext Context { get; init; }
        public required long CaseId { get; init; }
        public required string Serial { get; init; }
        public required long OtherCaseId { get; init; }
        public required string OtherSerial { get; init; }
        public required long PrimaryLeadId { get; init; }
        public required long OrderStatusId { get; init; }
        public required long ShipmentStatusId { get; init; }
    }

    private sealed record SeedGraph(long CaseId, string Serial, long OtherCaseId, string OtherSerial);

    /// <summary>
    /// Two commercial cases in one tenant, each with its own lead, plus the statuses a sales order
    /// and a shipment need. Two cases are the minimum that can prove the reader distinguishes
    /// them — with one case, a reader that ignores the column looks identical to one that uses it.
    /// </summary>
    private static async Task<SeedGraph> SeedAsync(TestDb db, Action<SeedContext> arrange)
    {
        const long orderStatusId = 97_431;
        const long shipmentStatusId = 97_432;
        long caseId, otherCaseId;
        string serial, otherSerial;

        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Tenant, Tenant, "Read-key customer");
            seed.SetupMasters.AddRange(
                Status(orderStatusId, "OrderStatus", "OPEN"),
                Status(shipmentStatusId, "ShipmentStatus", "READY"));
            var primary = Seed.Lead(seed, 97_401, Tenant, buyersName: "Read-key buyer");
            var other = Seed.Lead(seed, 97_402, Tenant, buyersName: "Second buyer");
            await seed.SaveChangesAsync();

            caseId = primary.CommercialCaseId;
            serial = primary.CommercialCaseReference;
            otherCaseId = other.CommercialCaseId;
            otherSerial = other.CommercialCaseReference;

            arrange(new SeedContext
            {
                Context = seed,
                CaseId = caseId,
                Serial = serial,
                OtherCaseId = otherCaseId,
                OtherSerial = otherSerial,
                PrimaryLeadId = primary.Id,
                OrderStatusId = orderStatusId,
                ShipmentStatusId = shipmentStatusId
            });
            await seed.SaveChangesAsync();
        }

        return new SeedGraph(caseId, serial, otherCaseId, otherSerial);
    }

    private static async Task<CommercialCaseDetail> ReadAsync(TestDb db, long caseId)
    {
        await using var context = db.ContextFor(Tenant);
        var service = new CommercialCaseQueryService(context, new StubTenant(Tenant));
        var detail = await service.GetAsync(Tenant, caseId, default);
        Assert.NotNull(detail);
        return detail!;
    }

    private static Order NewOrder(SeedContext seed, long id, string orderNo, long? leadId) => new()
    {
        Id = id,
        OrderNo = orderNo,
        LeadId = leadId,
        CustomerId = Tenant,
        BusinessUnitId = Tenant,
        StatusId = seed.OrderStatusId,
        OrderDate = Now,
        TotalAmount = 10m,
        PaidAmount = 0m,
        CreatedBy = "qa",
        CreatedOn = Now,
        IsActive = true
    };

    private static SetupMaster Status(long setupId, string type, string value) => new()
    {
        SetupId = setupId, SetupType = type, SetupCode = value, SetupValue = value,
        BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
    };
}
