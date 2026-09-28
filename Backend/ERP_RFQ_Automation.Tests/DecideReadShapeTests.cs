using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PERF-02 / PERF-03 (pilot audit 2026-09-28). Decide on a 1,500-line request downloaded the full
/// lead record (2.46 MB) to read the owner's name, and one "Create RFQ" click rebuilt the whole
/// decision workbench four times — the first of them inside the fit save, which built every line
/// only to return the fit. These pin the small reads that replace both.
/// </summary>
public sealed class DecideReadShapeTests
{
    private const long Tenant = 7_401;

    [Fact]
    public async Task Fit_save_returns_the_saved_assessment_without_rebuilding_the_workbench()
    {
        var workbench = new RecordingWorkbench();
        var controller = Controller(new SavingParticipation(), workbench);

        var response = await controller.SaveFitAssessment(77,
            new SaveFitAssessmentRequest(88, 1, null, "FIT", "No concerns.", []), default);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var fit = Assert.IsType<FitAssessmentDto>(ok.Value);
        Assert.Equal(3, fit.Version);
        Assert.Equal(0, workbench.FullBuilds);
        Assert.Equal([4_242L], workbench.FitReads);
    }

    [Fact]
    public async Task The_fit_read_is_the_same_object_the_workbench_shows()
    {
        using var db = new TestDb();
        var leadId = await ReconciledLeadAsync(db, 7_411, lines: 3);
        await using var context = db.ContextFor(Tenant);
        var lead = await context.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var saved = await new LeadParticipationService(context,
                new LeadDecisionService(context, new GrossMarginService(context)), new LeadOutcomeReasons(context))
            .RecordFitAssessmentAsync(Tenant, leadId, new RecordLeadFitAssessmentCommand(
                lead.CurrentRevisionId!.Value, lead.CurrentRevisionNumber, null, "CONDITIONAL", "Delivery is tight.",
                LeadParticipationService.GovernedFitCriterionCodes
                    .Select(code => new LeadFitCriterionCommand(code, code == "DELIVERY" ? "CONCERN" : "PASS",
                        code == "DELIVERY" ? "Delivery is tight." : null))
                    .ToArray(), $"decide-read-fit:{leadId}", "rep@nexora.test"));

        var service = new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context));
        var full = await service.GetAsync(Tenant, leadId);
        var slim = await service.GetFitAssessmentAsync(Tenant, saved.Id);

        Assert.Equal(JsonSerializer.Serialize(full.FitAssessment), JsonSerializer.Serialize(slim));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetFitAssessmentAsync(Tenant + 1, saved.Id));
    }

    [Fact]
    public async Task The_owner_read_carries_what_the_lead_record_said_about_the_owner()
    {
        using var db = new TestDb();
        var owned = await ReconciledLeadAsync(db, 7_421, lines: 2);
        var unowned = await ReconciledLeadAsync(db, 7_422, lines: 2);
        await using (var seed = db.ContextFor(Tenant))
        {
            var user = new User
            {
                FirstName = "Sara", LastName = "Haddad", Email = "sara@nexora.test", PasswordHash = "x",
                ImageUrl = string.Empty, Buid = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            var lead = await seed.Leads.SingleAsync(x => x.Id == owned);
            lead.AssignTo = user.Id;
            lead.AssignmentMethod = LeadAssignmentMethods.Manual;
            lead.AssignmentVersion = 4;
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var service = new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context));
        var repository = new ERP_RFQ_Automation.Repositories.LeadRepository(context);

        foreach (var leadId in new[] { owned, unowned })
        {
            var slim = await service.GetOwnerAsync(Tenant, leadId);
            var full = await repository.GetLeadByIdAsync(leadId, Tenant);
            Assert.NotNull(full);
            Assert.Equal(full!.AssignedToId, slim.AssignedToId);
            Assert.Equal(full.AssignedToFullName, slim.AssignedToFullName);
            Assert.Equal(full.AssignmentMethod, slim.AssignmentMethod);
            Assert.Equal(full.AssignmentVersion, slim.AssignmentVersion);
            Assert.Equal(full.Rfqno, slim.Rfqno);
        }
        Assert.Equal("Sara Haddad", (await service.GetOwnerAsync(Tenant, owned)).AssignedToFullName);
        Assert.Null((await service.GetOwnerAsync(Tenant, unowned)).AssignedToFullName);
        Assert.Null((await service.GetOwnerAsync(Tenant, unowned)).AssignedToId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetOwnerAsync(Tenant + 1, owned));
    }

    [Fact]
    public void The_owner_read_is_routed_beside_the_workbench_behind_the_lead_view_permission()
    {
        var method = typeof(LeadParticipationController).GetMethod(nameof(LeadParticipationController.GetOwner))!;
        Assert.Equal("owner", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>());
        Assert.Equal("Leads", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    // ------------------------------------------------------------------ fixture

    private static async Task<long> ReconciledLeadAsync(TestDb db, long ingestId, int lines)
    {
        await using var context = db.ContextFor(Tenant);
        if (!await context.BusinessUnits.AnyAsync(x => x.Id == Tenant))
        {
            Seed.BusinessUnit(context, Tenant);
            await context.SaveChangesAsync();
        }
        Seed.EmailConfig(context, ingestId + 50_000, Tenant);
        Seed.EmailIngest(context, ingestId, ingestId + 50_000, "NeedsReview");
        await context.SaveChangesAsync();
        var lead = new Lead
        {
            Rfqno = $"RFQ-{ingestId}", BuyersName = "Buyer", RecDate = DateTime.UtcNow, LeadSource = "ManualUpload",
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = Tenant, EmailIngestsId = ingestId,
            Clientemail = $"buyer{ingestId}@example.test", RequiresCommercialReview = true
        };
        for (var i = 1; i <= lines; i++)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = i.ToString(CultureInfo.InvariantCulture),
                ProductShortDescription = $"Spare part {ingestId}-{i}",
                Quantity = 1,
                UnitOfMeasure = "EA",
            });
        var created = await new LeadIdentityApplicationService(context).ReconcileAsync(lead, new LeadIntakeDescriptor(
            Guid.NewGuid(), "ManualUpload", $"decide-{ingestId}", null, null, "test", $"buyer{ingestId}@example.test",
            "RFQ", $"decide-{ingestId}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 100,
            $"decide-hash-{ingestId}".PadRight(64, '0')[..64], null, null, null, DateTimeOffset.UtcNow,
            LeadProcessingPath.Deterministic, false, 0, "User", "tester", $"test:decide-{ingestId}"));
        return created.LeadId;
    }

    private static LeadParticipationController Controller(ILeadParticipationService participation,
        ILeadDecisionWorkbenchService workbench)
    {
        var controller = new LeadParticipationController(
            participation, null!, workbench, null!, new AllowAll(), null!);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("businessUnitId", Tenant.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Email, "rep@nexora.test")
            ], "test"))
        };
        http.Request.Headers["Idempotency-Key"] = "decide-read-shape";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private sealed class RecordingWorkbench : ILeadDecisionWorkbenchService
    {
        public int FullBuilds { get; private set; }
        public List<long> FitReads { get; } = new();

        public Task<LeadDecisionWorkbenchDto> GetAsync(long businessUnitId, long leadId, CancellationToken ct = default)
        {
            FullBuilds++;
            throw new InvalidOperationException("The fit save must not rebuild the workbench.");
        }

        public Task<FitAssessmentDto> GetFitAssessmentAsync(long businessUnitId, long fitAssessmentId, CancellationToken ct = default)
        {
            FitReads.Add(fitAssessmentId);
            return Task.FromResult(new FitAssessmentDto(3, "FIT", "No concerns.", [], "rep@nexora.test", DateTimeOffset.UtcNow));
        }

        public Task<LeadOwnerDto> GetOwnerAsync(long businessUnitId, long leadId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SavingParticipation : ILeadParticipationService
    {
        public Task<LeadFitAssessmentResult> RecordFitAssessmentAsync(
            long businessUnitId, long leadId, RecordLeadFitAssessmentCommand command, CancellationToken ct = default) =>
            Task.FromResult(new LeadFitAssessmentResult(4_242, leadId, command.ExpectedLeadRevisionId, 3, "test", "FIT",
                true, "{}", DateTimeOffset.UtcNow));

        public Task<LeadParticipationResult> CommitDecisionAsync(
            long businessUnitId, long leadId, CommitLeadParticipationCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LeadParticipationResult?> GetCurrentDecisionAsync(
            long businessUnitId, long leadId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class AllowAll : ICommercialAccessContext
    {
        public Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default) =>
            Task.FromResult<CommercialActorScope?>(null);
        public Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default) => Task.FromResult(true);
    }
}
