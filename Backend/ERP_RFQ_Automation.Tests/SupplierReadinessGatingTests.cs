using System.Security.Claims;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.BusinessUnit;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Two gates, two moments (owner decision 2026-09-16: "tick who to ask, send, next line").
///
/// <para>ASKING a supplier for a price is not a commitment: it needs an address and a supplier
/// nobody has shut out (inactive, blocked, high or blocked risk). Both surfaces that send the
/// request — <c>ProcurementApplicationService.SupplierAskBlockingReasons</c> and
/// <c>SupplierController.SupplierRfqBlockingReasons</c> — must refuse exactly those.</para>
///
/// <para>Approval, verification, compliance and readiness no longer stop the request. They stop
/// the commitment: picking that supplier's price and raising an order, which still run the full
/// <c>SupplierRfqBlockingReasons</c> rule.</para>
/// </summary>
public sealed class SupplierReadinessGatingTests
{
    /// <summary>Every flip here must independently stop the request being sent.</summary>
    public static TheoryData<string> AskBlockingDimensions() =>
    [
        "inactive",
        "no-contact-email",
        "risk-high",
        "risk-blocked",
        "governance-blocked"
    ];

    /// <summary>Every flip here is checked when a price is picked, and must NOT stop the request.</summary>
    public static TheoryData<string> ApprovalOnlyDimensions() =>
    [
        "governance-unverified",
        "verification-pending",
        "compliance-not-cleared",
        "readiness-review-required"
    ];

    [Theory]
    [MemberData(nameof(AskBlockingDimensions))]
    public async Task The_procurement_service_refuses_to_ask_a_supplier_nobody_can_send_to(string dimension)
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, $"gate-{dimension}", "qa", $"corr-gate-{dimension}")));
        var candidate = Assert.Single(created.Candidates);

        await using (var setup = fixture.Context())
        {
            var supplier = await setup.Suppliers.SingleAsync(x => x.Id == candidate.SupplierId);
            ApplyBlockingDimension(supplier, dimension);
            await setup.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                $"gate-prepare-{dimension}", "qa", $"corr-gate-prepare-{dimension}"))));

        await using var verify = fixture.Context();
        Assert.Empty(await verify.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().ToListAsync());
        Assert.Empty(await verify.ProcurementOutboxMessages.ToListAsync());
    }

    [Theory]
    [MemberData(nameof(ApprovalOnlyDimensions))]
    public async Task An_unapproved_supplier_can_still_be_asked_for_a_price(string dimension)
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, $"ask-{dimension}", "qa", $"corr-ask-{dimension}")));
        var candidate = Assert.Single(created.Candidates);

        await using (var setup = fixture.Context())
        {
            var supplier = await setup.Suppliers.SingleAsync(x => x.Id == candidate.SupplierId);
            ApplyBlockingDimension(supplier, dimension);
            await setup.SaveChangesAsync();
        }

        var prepared = await fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                $"ask-prepare-{dimension}", "qa", $"corr-ask-prepare-{dimension}")));
        Assert.True(prepared.SupplierSolicitationId > 0);
    }

    [Fact]
    public async Task The_rep_chooses_how_many_to_ask_for_and_the_shortfall_is_only_the_default()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, "ask-quantity", "qa", "corr-ask-quantity")));
        var candidate = Assert.Single(created.Candidates);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "ask-quantity-zero", "qa", "corr-ask-quantity-zero", Quantity: 0m))));

        var prepared = await fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
                "ask-quantity-7", "qa", "corr-ask-quantity-7", Quantity: 7m)));

        await using var verify = fixture.Context();
        var createdEvent = await verify.ProcurementEvents
            .Where(x => x.AggregateType == "SupplierSolicitation" && x.AggregateId == prepared.SupplierSolicitationId
                && x.EventType == "SUPPLIER_RFQ_CREATED")
            .SingleAsync();
        Assert.Contains("\"Quantity\":7", createdEvent.PayloadJson);
    }

    [Theory]
    [InlineData(SolicitationStatus.DeliveryFailed)]
    [InlineData(SolicitationStatus.Sent)]
    [InlineData(SolicitationStatus.Responded)]
    public async Task The_rep_can_ask_the_same_supplier_again(SolicitationStatus earlier)
    {
        // The last email failed, the supplier has not answered, or their price has expired: asking
        // again is the rep's choice and makes a new numbered request.
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, $"again-{earlier}", "qa", $"corr-again-{earlier}")));
        var candidate = Assert.Single(created.Candidates);
        var first = await fixture.Execute(service => service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
            fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, created.Version,
            $"again-first-{earlier}", "qa", $"corr-again-first-{earlier}")));
        await using (var setup = fixture.Context())
        {
            var row = await setup.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().SingleAsync(x => x.Id == first.SupplierSolicitationId);
            row.Status = earlier;
            await setup.SaveChangesAsync();
        }

        var second = await fixture.Execute(service => service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
            fixture.BusinessUnitId, created.Id, candidate.SupplierId, null, first.SourcingCaseVersion,
            $"again-second-{earlier}", "qa", $"corr-again-second-{earlier}")));

        Assert.NotEqual(first.SupplierSolicitationId, second.SupplierSolicitationId);
    }

    [Theory]
    [MemberData(nameof(AskBlockingDimensions))]
    public async Task The_supplier_controller_refuses_to_compose_a_request_nobody_can_send(string dimension)
    {
        var supplier = ReadySupplier();
        ApplyBlockingDimension(supplier, dimension);
        var controller = Controller(new StubSupplierRepository(supplier));

        var result = await controller.ComposeQuoteEmail(new BatchQuoteRequestDTO
        {
            SupplierId = supplier.Id,
            Items = [new QuoteItemDTO { PartNumber = "P-1", Quantity = 1 }]
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Theory]
    [MemberData(nameof(ApprovalOnlyDimensions))]
    public async Task The_supplier_controller_composes_a_request_to_an_unapproved_supplier(string dimension)
    {
        var supplier = ReadySupplier();
        ApplyBlockingDimension(supplier, dimension);
        var controller = Controller(new StubSupplierRepository(supplier));

        var result = await controller.ComposeQuoteEmail(new BatchQuoteRequestDTO
        {
            SupplierId = supplier.Id,
            Items = [new QuoteItemDTO { PartNumber = "P-1", Quantity = 1 }]
        });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_fully_ready_supplier_is_allowed_through_the_controller_gate()
    {
        var controller = Controller(new StubSupplierRepository(ReadySupplier()));

        var result = await controller.ComposeQuoteEmail(new BatchQuoteRequestDTO
        {
            SupplierId = ReadySupplier().Id,
            Items = [new QuoteItemDTO { PartNumber = "P-1", Quantity = 1 }]
        });

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_candidate_view_reports_the_same_blockers_the_gate_enforces()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        await using (var setup = fixture.Context())
        {
            var supplier = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            supplier.RiskStatus = SupplierRiskStatuses.Blocked;
            await setup.SaveChangesAsync();
        }

        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            new CreateSourcingCaseCommand(fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId,
                10, false, "gate-view", "qa", "corr-gate-view")));

        var candidate = Assert.Single(created.Candidates);
        Assert.False(candidate.EligibleForSupplierRfq);
        Assert.NotEmpty(candidate.BlockingReasons);
    }

    private static void ApplyBlockingDimension(Supplier supplier, string dimension)
    {
        switch (dimension)
        {
            case "inactive": supplier.IsActive = false; break;
            case "no-contact-email": supplier.ContactEmail = null; break;
            case "governance-unverified":
                supplier.GovernanceStatus = SupplierGovernanceStatuses.Unverified; break;
            case "verification-pending":
                supplier.VerificationStatus = SupplierVerificationStatuses.Pending; break;
            case "compliance-not-cleared":
                supplier.ComplianceStatus = SupplierComplianceStatuses.Pending; break;
            case "risk-high": supplier.RiskStatus = SupplierRiskStatuses.High; break;
            case "risk-blocked": supplier.RiskStatus = SupplierRiskStatuses.Blocked; break;
            case "governance-blocked": supplier.GovernanceStatus = SupplierGovernanceStatuses.Blocked; break;
            case "readiness-review-required":
                supplier.ReadinessStatus = SupplierReadinessStatuses.ReviewRequired; break;
            default: throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "Unknown readiness dimension.");
        }
    }

    private static Supplier ReadySupplier() => new()
    {
        Id = 98_001,
        Buid = 98_000,
        Name = "Ready Supplier",
        ContactEmail = "ready@example.test",
        ImageUrl = "n/a",
        IsActive = true,
        GovernanceStatus = SupplierGovernanceStatuses.Approved,
        VerificationStatus = SupplierVerificationStatuses.Verified,
        ComplianceStatus = SupplierComplianceStatuses.Cleared,
        RiskStatus = SupplierRiskStatuses.Low,
        ReadinessStatus = SupplierReadinessStatuses.Ready,
        CreatedBy = "qa",
        CreatedOn = DateTime.UtcNow
    };

    private static SupplierController Controller(ISupplierRepository repository)
    {
        var identity = new ClaimsIdentity([new Claim("businessUnitId", "98000")], "test");
        return new SupplierController(repository, new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static async Task MakeSourcingReadyAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        await context.SaveChangesAsync();
    }

    private sealed class StubSupplierRepository(Supplier supplier) : ISupplierRepository
    {
        public Task<Supplier> GetByIdAsync(long id, long businessUnitId) => Task.FromResult(supplier);

        public Task<(IEnumerable<SupplierResponseDTO>, int TotalCount)> GetAllAsync(int pageNumber, int pageSize,
            long? id, string? name, string? contactEmail, long? currencyId, bool? isActive, string? docId,
            long businessUnitId) => Task.FromResult<(IEnumerable<SupplierResponseDTO>, int)>(([], 0));

        public Task AddAsync(Supplier value) => Task.CompletedTask;
        public Task UpdateAsync(Supplier value, long businessUnitId) => Task.CompletedTask;
        public Task DeleteAsync(long id, long businessUnitId) => Task.CompletedTask;

        public Task<List<SupplierSearchResultDTO>> SearchSuppliersAsync(string? searchTerm,
            string? productCategory, long businessUnitId, string? tier = null)
            => Task.FromResult(new List<SupplierSearchResultDTO>());
    }
}
