using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The monthly hard token limit is the one AI control with no allow-list exemption below it,
/// and zero is a legal number.
///
/// <para>A tenant carrying <c>MonthlyHardTokenLimit = 0</c> reads OPEN on every other control
/// in the extraction pre-flight and then has every single document refused in the token ledger
/// with <c>hard_budget_exceeded</c> — a code that triages as a broken extractor rather than as
/// a setting somebody typed. It is a kill switch wearing a budget's clothes, and a console that
/// offers it as a number invites an operator to reach for it when they mean "no AI for this
/// tenant". That is <c>IsEnabled = false</c>, which says so on every screen that shows it.</para>
/// </summary>
public sealed class AiPolicyBudgetGuardTests
{
    [Fact]
    public async Task UpdateAiPolicy_RefusesAZeroMonthlyHardTokenLimit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var refused = await Controller(context).UpdateAiPolicy(
            id: 4_242, Request(monthlyHardTokenLimit: 0), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Contains("refuses every document", bad.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAiPolicy_StillAcceptsARealCeilingAndNoCeilingAtAll()
    {
        // The guard is aimed at zero alone: one token is a (silly) budget, and unset is the
        // documented "no monthly ceiling". Both get past validation and fail later, on the
        // tenant lookup, which is how this test knows validation let them through.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        foreach (var limit in new long?[] { 1, 2_000_000, null })
        {
            var result = await controller.UpdateAiPolicy(
                id: 4_242, Request(monthlyHardTokenLimit: limit), CancellationToken.None);
            Assert.IsType<NotFoundResult>(result.Result);
        }
    }

    private static UpdateTenantAiPolicyRequest Request(long? monthlyHardTokenLimit) => new()
    {
        IsEnabled = true,
        ExternalProcessingAllowed = false,
        AllowedPurposes = ["RfqExtraction"],
        MonthlyHardTokenLimit = monthlyHardTokenLimit,
        ExternalDependencyCeilingPercent = 10m,
        RetentionDays = 30,
        Reason = "Pinning the budget guard."
    };

    private static TenantsController Controller(ErpRfqAutomationContext context)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Owner() }
            }
        };
    }

    private static ClaimsPrincipal Owner() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "operator@example.test"),
        new Claim("platformRole", "Owner")
    ], "Platform"));
}
