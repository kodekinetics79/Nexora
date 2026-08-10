using System.Security.Claims;
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
/// Supplier readiness is the gate that stops an unapproved, unverified, non-compliant or
/// high-risk supplier from receiving a Supplier RFQ or a purchase order. It is enforced by
/// two INDEPENDENT copies of the same rule — one in
/// <c>ProcurementApplicationService.SupplierRfqBlockingReasons</c> and one in
/// <c>SupplierController.SupplierRfqBlockingReasons</c>. Duplicated governance logic is only
/// safe while both copies agree, so these tests drive every readiness dimension through both
/// surfaces and require both to refuse.
/// </summary>
public sealed class SupplierReadinessGatingTests
{
    /// <summary>Every field flip here must independently block outreach.</summary>
    public static TheoryData<string> BlockingDimensions() =>
    [
        "inactive",
        "no-contact-email",
        "governance-unverified",
        "verification-pending",
        "compliance-not-cleared",
        "risk-high",
        "risk-blocked",
        "readiness-review-required"
    ];

    [Theory]
    [MemberData(nameof(BlockingDimensions))]
    public async Task The_procurement_service_refuses_to_prepare_a_supplier_rfq(string dimension)
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
    [MemberData(nameof(BlockingDimensions))]
    public async Task The_supplier_controller_refuses_to_compose_an_outreach_email(string dimension)
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
            string? productCategory, long businessUnitId) => Task.FromResult(new List<SupplierSearchResultDTO>());
    }
}
