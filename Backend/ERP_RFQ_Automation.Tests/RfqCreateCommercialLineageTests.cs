using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// POST /api/Rfq without a LeadId used to be a hard 400 ("A tenant-owned lead is
/// required...") delivered as a bare string the frontend could not render. The core fix
/// keeps the serial-lineage invariant — every RFQ belongs to a commercial case shared by
/// its Lead and Quote — by generating a governed manual-origin shell lead in the SAME
/// transaction as the RFQ. These tests pin:
///
///  * the shell lead is lineage-valid (commercial case + Nexora Serial, tenant-owned
///    customer, "manual-rfq" provenance, born CONVERTED_TO_RFQ so it never re-enters
///    the triage queues) and the RFQ inherits exactly that identity;
///  * refusals are tenant-honest (foreign lead / foreign customer / no customer) and
///    atomic (a failed RFQ insert leaves no orphan shell lead behind);
///  * every 4xx the controller now returns is an RFC 7807 ProblemDetails with a traceId,
///    because the previous bare-string bodies are what hid this failure from users.
/// </summary>
public sealed class RfqCreateCommercialLineageTests
{
    private const long Bu = 9_600;
    private const long OtherBu = 9_601;
    private const long CustomerId = 9_610;
    private const long LeadStatusConvertedId = 9_620;
    private const long RfqStatusDraftId = 9_621;

    // ── repository: shell-lead generation ────────────────────────────────────────────

    [Fact]
    public async Task Create_without_lead_generates_a_lineage_valid_shell_lead_in_the_same_transaction()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var rfq = NewRfq(customerId: CustomerId, leadId: null);
        rfq.BuyersName = "Manually Entered Buyer";
        await new RfqRepository(context).AddAsync(rfq);

        await using var assertContext = db.ContextFor(null);
        var persistedRfq = await assertContext.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfq.Id);
        Assert.NotNull(persistedRfq.LeadId);
        Assert.Matches($@"^NXR-RFQ-{Bu}-\d{{4}}-\d{{8}}$", persistedRfq.Rfqno);
        Assert.Equal(RfqStatusDraftId, persistedRfq.RfqstatusId);

        var lead = await assertContext.Leads.AsNoTracking().SingleAsync(l => l.Id == persistedRfq.LeadId!.Value);
        // Tenant + customer + provenance the task demands of a shell lead.
        Assert.Equal(Bu, lead.BusinessUnitId);
        Assert.Equal(CustomerId, lead.CustomerId);
        Assert.Equal("CUSTOMER_CONFIRMED", lead.CustomerMatchStatus); // fits the varchar(32) column
        Assert.Null(lead.ContactId); // contact stays honestly unresolved
        Assert.Equal("manual-rfq", lead.LeadSource);
        Assert.Equal("ops@nexora.example.t", lead.CreatedBy); // actor, truncated to the Leads varchar(20)
        Assert.Null(lead.Aiconfidence);
        Assert.False(lead.RequiresCommercialReview); // human-entered facts: no extraction review debt
        Assert.Equal("Manually Entered Buyer", lead.BuyersName);

        // Lifecycle: born already converted, so it never appears as an untriaged lead
        // and cannot be converted into a second RFQ.
        Assert.Equal(LeadStatusConvertedId, lead.LeadStatusId);

        // Serial lineage: the lead owns a commercial case and the RFQ inherited it.
        Assert.True(lead.CommercialCaseId > 0);
        Assert.False(string.IsNullOrWhiteSpace(lead.CommercialCaseReference));
        Assert.Equal(lead.CommercialCaseId, persistedRfq.CommercialCaseId);
        Assert.Equal(lead.CommercialCaseReference, persistedRfq.NexoraSerial);
        Assert.Equal(CustomerId, persistedRfq.CustomerId);
        Assert.NotNull(await assertContext.CommercialCases.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == lead.CommercialCaseId));
        Assert.Single(await assertContext.LeadStatusHistories.AsNoTracking()
            .Where(h => h.LeadId == lead.Id && h.EventType == "Created").ToListAsync());
    }

    [Fact]
    public async Task Create_without_lead_and_without_customer_is_refused_with_an_actionable_message()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new RfqRepository(context).AddAsync(NewRfq(customerId: null, leadId: null)));

        Assert.Contains("customer", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var assertContext = db.ContextFor(null);
        Assert.Empty(await assertContext.Rfqs.AsNoTracking().ToListAsync());
        Assert.Empty(await assertContext.Leads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_without_lead_with_a_foreign_tenant_customer_is_refused()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            Seed.Customer(seed, 9_710, OtherBu, "Foreign Customer");
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new RfqRepository(context).AddAsync(NewRfq(customerId: 9_710, leadId: null)));

        Assert.Contains("customer", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var assertContext = db.ContextFor(null);
        Assert.Empty(await assertContext.Leads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_with_a_foreign_tenant_lead_is_refused_without_leaking_existence()
    {
        using var db = new TestDb();
        long foreignLeadId;
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            var foreignLead = Seed.Lead(seed, 9_720, OtherBu);
            await seed.SaveChangesAsync();
            foreignLeadId = foreignLead.Id;
        }

        await using var context = db.ContextFor(Bu);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new RfqRepository(context).AddAsync(NewRfq(customerId: null, leadId: foreignLeadId)));

        Assert.Contains("does not exist in this business unit", error.Message);
        await using var assertContext = db.ContextFor(null);
        Assert.Empty(await assertContext.Rfqs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Create_with_a_tenant_lead_keeps_the_existing_lineage_behaviour()
    {
        using var db = new TestDb();
        long leadId;
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            var lead = Seed.Lead(seed, 9_730, Bu);
            await seed.SaveChangesAsync();
            lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");
            await seed.SaveChangesAsync();
            leadId = lead.Id;
        }

        await using var context = db.ContextFor(Bu);
        var rfq = NewRfq(customerId: CustomerId, leadId: leadId);
        await new RfqRepository(context).AddAsync(rfq);

        await using var assertContext = db.ContextFor(null);
        var persistedRfq = await assertContext.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfq.Id);
        var lead2 = await assertContext.Leads.AsNoTracking().SingleAsync(l => l.Id == leadId);
        Assert.Equal(leadId, persistedRfq.LeadId);
        Assert.Equal(lead2.CommercialCaseId, persistedRfq.CommercialCaseId);
        Assert.Equal(lead2.CommercialCaseReference, persistedRfq.NexoraSerial);
        Assert.Matches($@"^NXR-RFQ-{Bu}-\d{{4}}-\d{{8}}$", persistedRfq.Rfqno);
        // No second lead was invented for a request that already had one.
        Assert.Single(await assertContext.Leads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Shell_lead_is_rolled_back_when_the_rfq_insert_fails()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            // Lead statuses exist, but the tenant has NO RFQStatus DRAFT row, so the
            // RFQ half of the transaction fails after the shell lead was inserted.
            Seed.EnsureBusinessUnit(seed, Bu);
            Seed.Customer(seed, CustomerId, Bu, "Lineage Customer");
            var converted = Seed.LeadStatus(seed, LeadStatusConvertedId, Bu, "Converted to RFQ");
            converted.SetupCode = "CONVERTED_TO_RFQ";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(context).AddAsync(NewRfq(customerId: CustomerId, leadId: null)));

        await using var assertContext = db.ContextFor(null);
        Assert.Empty(await assertContext.Rfqs.AsNoTracking().ToListAsync());
        Assert.Empty(await assertContext.Leads.AsNoTracking().ToListAsync());
        Assert.Empty(await assertContext.CommercialCases.AsNoTracking().ToListAsync());
    }

    // ── controller: end-to-end create and RFC 7807 bodies ────────────────────────────

    [Fact]
    public async Task Controller_create_without_lead_returns_201_with_the_inherited_serial()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var controller = Controller(new RfqRepository(context));
        var result = await controller.Create(new RfqCreateRequestDTO
        {
            BuyersName = "Manually Entered Buyer",
            RecDate = DateTime.UtcNow,
            CustomerId = CustomerId,
            Rfqitems =
            [
                new RfqitemCreateRequestDTO { LineItemNo = "1", ProductShortName = "Widget", Quantity = 3 }
            ]
        });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<RfqResponseDTO>(created.Value);
        Assert.NotNull(dto.LeadId);
        Assert.False(string.IsNullOrWhiteSpace(dto.NexoraSerial));
        Assert.Equal(CustomerId, dto.CustomerId);
        Assert.Single(dto.Rfqitems);
        // FX guard (coexistence with the header-currency work): currency-silent lines
        // stay currency-silent — nothing invented a currency during creation.
        Assert.Null(Assert.Single(dto.Rfqitems).CurrencyId);
    }

    [Fact]
    public async Task Controller_create_without_lead_or_customer_returns_a_renderable_problem()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var controller = Controller(new RfqRepository(context));
        var result = await controller.Create(new RfqCreateRequestDTO { RecDate = DateTime.UtcNow });

        var problem = AssertProblem(result.Result, StatusCodes.Status400BadRequest);
        Assert.Contains("customer", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Controller_create_with_foreign_tenant_lead_returns_a_renderable_problem()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed);
            Seed.Lead(seed, 9_740, OtherBu);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Bu);
        var controller = Controller(new RfqRepository(context));
        var result = await controller.Create(new RfqCreateRequestDTO
        {
            RecDate = DateTime.UtcNow,
            LeadId = 9_740
        });

        var problem = AssertProblem(result.Result, StatusCodes.Status400BadRequest);
        Assert.Contains("does not exist in this business unit", problem.Detail);
    }

    [Fact]
    public async Task Controller_create_conflict_is_a_renderable_problem()
    {
        var controller = Controller(new ThrowingRfqRepository(
            new InvalidOperationException("DRAFT is not configured and active for this tenant.")));

        var result = await controller.Create(new RfqCreateRequestDTO { RecDate = DateTime.UtcNow });

        var problem = AssertProblem(result.Result, StatusCodes.Status409Conflict);
        Assert.Contains("DRAFT", problem.Detail);
    }

    [Fact]
    public async Task Controller_list_paging_errors_are_renderable_problems()
    {
        var controller = Controller(new ThrowingRfqRepository(new InvalidOperationException("unused")));

        AssertProblem((await controller.GetAll(pageNumber: 0)).Result, StatusCodes.Status400BadRequest);
        AssertProblem((await controller.GetAll(pageSize: 0)).Result, StatusCodes.Status400BadRequest);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>BU + customer + the two lifecycle statuses the create path resolves.</summary>
    private static void SeedTenant(ErpRfqAutomationContext seed)
    {
        Seed.EnsureBusinessUnit(seed, Bu);
        Seed.EnsureBusinessUnit(seed, OtherBu);
        Seed.Customer(seed, CustomerId, Bu, "Lineage Customer");
        var converted = Seed.LeadStatus(seed, LeadStatusConvertedId, Bu, "Converted to RFQ");
        converted.SetupCode = "CONVERTED_TO_RFQ";
        seed.SetupMasters.Add(new SetupMaster
        {
            SetupId = RfqStatusDraftId,
            SetupType = "RFQStatus",
            SetupCode = "DRAFT",
            SetupValue = "Draft",
            BusinessUnitId = Bu,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
    }

    private static Rfq NewRfq(long? customerId, long? leadId) => new()
    {
        Rfqno = string.Empty,
        BuyersName = "Manually Entered Buyer",
        RecDate = DateTime.UtcNow,
        BusinessUnitId = Bu,
        LeadId = leadId,
        CustomerId = customerId,
        CreatedBy = "ops@nexora.example.test",
        CreatedDate = DateTime.UtcNow,
        NoOfLineItems = 1,
        Rfqitems =
        {
            new Rfqitem
            {
                LineItemNo = "1",
                ProductShortName = "Widget",
                Quantity = 3,
                CreatedBy = "ops@nexora.example.test",
                CreatedDate = DateTime.UtcNow
            }
        }
    };

    private static RfqController Controller(IRfqRepository repository)
        => new(repository, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    TraceIdentifier = "trace-rfq-tests",
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.Email, "ops@nexora.example.test")
                    ], "test"))
                }
            }
        };

    /// <summary>The renderable RFC 7807 contract every 4xx now honours: a ProblemDetails
    /// body with status, a title, a detail, and the request's traceId.</summary>
    private static ProblemDetails AssertProblem(ActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal(expectedStatus, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.Equal("trace-rfq-tests", Assert.Contains("traceId", problem.Extensions));
        return problem;
    }

    private sealed class ThrowingRfqRepository(Exception exception) : IRfqRepository
    {
        public Task AddAsync(Rfq rfq) => throw exception;
        public Task<(IEnumerable<RfqResponseDTO>, int TotalItems)> GetAllAsync(
            long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null,
            bool? isActive = null, long? assignedToId = null, string? createdBy = null,
            long? rfqStatusId = null, string? rfqStatusCode = null, string? readiness = null)
            => Task.FromResult<(IEnumerable<RfqResponseDTO>, int)>(([], 0));
        public Task<RfqResponseDTO> GetByIdAsync(long id, long businessUnitId) => throw exception;
        public Task UpdateAsync(Rfq rfq) => throw exception;
        public Task DeleteAsync(long id, long businessUnitId) => throw exception;
        public Task<long> ApproveAsync(long id, string approvedBy, long businessUnitId, long? customerId = null) => throw exception;
        public Task<List<RFQTypeLookupDTO>> GetRFQTypeAsync() => throw exception;
        public Task<RfqStatsDTO> GetRfqStatsAsync(long businessUnitId) => throw exception;
    }
}
