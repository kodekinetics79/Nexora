using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Tenant creation is where the platform decides whether it will ever be paid, so these tests
/// treat "this tenant can be created without anybody being charged" as a defect of the same
/// severity as a data leak.
///
/// <para><b>The state they pin.</b> Provisioning used to take a name, a slug and an OPTIONAL
/// plan — the portal's own help text read "a tenant without a plan runs without plan limits".
/// Downstream, <c>BillingStatementService.BuildLines</c> emits the base-subscription line only
/// when a plan exists, so every plan-less tenant was metered for usage and charged nothing,
/// forever, with no signal anywhere that it was happening. A trial was not expressible at all,
/// which meant every "trial" was in fact permanent free service.</para>
/// </summary>
public sealed class TenantProvisioningCommercialTests
{
    private sealed class NullAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string targetType, string targetId,
            object? metadata = null, long? actAsTenantId = null, HttpContext? httpContext = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TenantsController Controller(ErpRfqAutomationContext context)
    {
        var controller = new TenantsController(
            context, new NullAudit(), NullLogger<TenantsController>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new MultiTenancy.TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("email", "owner@nexora.app"), new Claim("platformRole", "Owner")], "Platform"))
            }
        };
        return controller;
    }

    private static async Task<Plan> PricedPlanAsync(ErpRfqAutomationContext context, decimal? price = 499m)
    {
        var plan = new Plan { Code = $"pro-{Guid.NewGuid():N}", Name = "Pro", MonthlyPriceUsd = price };
        context.Set<Plan>().Add(plan);
        await context.SaveChangesAsync();
        return plan;
    }

    private static ProvisionTenantRequest Request(string slug) => new()
    {
        Name = $"Tenant {slug}",
        Slug = slug,
        BaseCurrencyCode = "USD",
        AdminEmail = $"admin@{slug}.example",
        AdminFirstName = "Founding",
        AdminLastName = "Admin"
    };

    private static string ErrorOf(ActionResult<ProvisionTenantResponse> result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        return bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value)!.ToString()!;
    }

    [Fact]
    public async Task A_billable_tenant_cannot_be_created_without_a_plan()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        // The default mode is Billable and no plan is supplied — the exact shape the old portal
        // submitted on every provision.
        var result = await Controller(context).Provision(Request("no-plan-tenant"), CancellationToken.None);

        Assert.Contains("must be assigned a plan", ErrorOf(result));

        await using var verify = db.ContextFor(null);
        Assert.False(await verify.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Slug == "no-plan-tenant"));
    }

    [Fact]
    public async Task Free_service_is_possible_only_when_somebody_names_the_reason()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var unexplained = Request("unexplained-freebie");
        unexplained.BillingMode = nameof(TenantBillingMode.Internal);
        Assert.Contains("billingModeReason is required",
            ErrorOf(await controller.Provision(unexplained, CancellationToken.None)));

        var explained = Request("explained-freebie");
        explained.BillingMode = nameof(TenantBillingMode.Internal);
        explained.BillingModeReason = "Internal support workspace — never invoiced.";
        var created = Assert.IsType<CreatedAtActionResult>(
            (await controller.Provision(explained, CancellationToken.None)).Result);

        // The exemption is on the record and it is loud: the posture returned to the operator says
        // in words that this tenant consumes the platform without paying for it.
        var response = Assert.IsType<ProvisionTenantResponse>(created.Value);
        Assert.Equal(nameof(TenantBillingMode.Internal), response.Billing.Mode);
        Assert.Contains(response.Billing.Warnings, w => w.Contains("without being"));
    }

    [Fact]
    public async Task A_trial_must_carry_the_date_it_converts()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var openEnded = Request("open-ended-trial");
        openEnded.BillingMode = nameof(TenantBillingMode.Trial);
        openEnded.BillingModeReason = "30-day evaluation agreed with the buyer.";
        Assert.Contains("trialEndsOn", ErrorOf(await controller.Provision(openEnded, CancellationToken.None)));

        // A date already in the past is the same leak wearing a date field.
        var alreadyExpired = Request("already-expired-trial");
        alreadyExpired.BillingMode = nameof(TenantBillingMode.Trial);
        alreadyExpired.BillingModeReason = "30-day evaluation agreed with the buyer.";
        alreadyExpired.TrialEndsOn = DateTime.UtcNow.AddDays(-1);
        Assert.Contains("not in the future",
            ErrorOf(await controller.Provision(alreadyExpired, CancellationToken.None)));

        var bounded = Request("bounded-trial");
        bounded.BillingMode = nameof(TenantBillingMode.Trial);
        bounded.BillingModeReason = "30-day evaluation agreed with the buyer.";
        bounded.TrialEndsOn = DateTime.UtcNow.AddDays(30);
        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Provision(bounded, CancellationToken.None)).Result).Value);

        Assert.NotNull(response.Tenant.TrialEndsOn);
        Assert.Contains(response.Billing.Warnings, w => w.Contains("converts on"));
    }

    [Fact]
    public async Task An_unpriced_plan_is_reported_rather_than_charged_as_zero()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context, price: null);

        var request = Request("unpriced-plan-tenant");
        request.PlanId = plan.Id;

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        // BuildLines would emit a base line of 0.00 with only a SourceNote to explain it. The
        // operator learns about that here, at creation, not from a quarter-end reconciliation.
        Assert.Contains(response.Billing.Warnings, w => w.Contains("no monthly price"));
    }

    [Fact]
    public async Task An_unpinned_rate_card_is_flagged_because_pricing_would_float()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context);

        var request = Request("floating-price-tenant");
        request.PlanId = plan.Id;

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        Assert.Equal(nameof(TenantBillingMode.Billable), response.Billing.Mode);
        Assert.Equal(plan.Code, response.Billing.PlanCode);
        Assert.Contains(response.Billing.Warnings, w => w.Contains("No rate card pinned"));
    }

    [Fact]
    public async Task A_fully_specified_billable_tenant_carries_no_revenue_warnings()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context);
        var card = new ERP_RFQ_Automation.Billing.RateCard
        {
            Code = "standard-2026", Currency = "USD", EffectiveFromUtc = DateTime.UtcNow.AddYears(-1),
            IsActive = true
        };
        context.Set<ERP_RFQ_Automation.Billing.RateCard>().Add(card);
        await context.SaveChangesAsync();

        var request = Request("clean-commercials-tenant");
        request.PlanId = plan.Id;
        request.RateCardId = card.Id;
        request.PaymentTermsDays = 30;
        request.BillingContactEmail = "ap@customer.example";
        request.ContractStartOn = DateTime.UtcNow.Date;
        request.ContractEndOn = DateTime.UtcNow.Date.AddYears(1);

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        // An empty warning list is the contract: this customer will be charged exactly as intended.
        Assert.Empty(response.Billing.Warnings);
        Assert.Equal("standard-2026", response.Billing.RateCardCode);
        Assert.Equal(30, response.Tenant.PaymentTermsDays);
        Assert.Equal("ap@customer.example", response.Tenant.BillingContactEmail);
        Assert.NotNull(response.Tenant.BillingStartsOn);
    }

    [Fact]
    public async Task Statements_reach_somebody_even_when_no_billing_contact_was_typed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context);

        var request = Request("no-ap-contact-tenant");
        request.PlanId = plan.Id;

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        // An invoice with no recipient is an invoice nobody pays. The founding administrator is a
        // defensible fallback; silence is not.
        Assert.Equal(request.AdminEmail, response.Tenant.BillingContactEmail);
        Assert.Equal("owner@nexora.app", response.Tenant.AccountOwnerEmail);
    }

    [Fact]
    public async Task The_company_a_customer_registered_as_is_what_gets_stored()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context);

        var request = Request("full-identity-tenant");
        request.PlanId = plan.Id;
        request.LegalName = "Full Identity Trading Company LLC";
        request.RegistrationNumber = "1010123456";
        request.TaxNumber = "300012345600003";
        request.CountryCode = "sa";
        request.Industry = "Industrial supply";
        request.Website = "https://full-identity.example";
        request.AddressLine1 = "King Fahd Road";
        request.City = "Riyadh";
        request.PostalCode = "12345";
        request.Phone = "+966 11 000 0000";
        request.ContactEmail = "info@full-identity.example";
        request.BaseCurrencyCode = "sar";
        request.TimeZoneId = "Asia/Riyadh";
        request.Locale = "en-GB";
        request.DataRegion = "me-central-1";

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        // Codes are stored in their canonical case regardless of how they were typed, because
        // downstream lookups (currency rows, country reference data) match on them exactly.
        Assert.Equal("SA", response.Tenant.CountryCode);
        Assert.Equal("SAR", response.Tenant.BaseCurrencyCode);
        Assert.Equal("Full Identity Trading Company LLC", response.Tenant.LegalName);
        Assert.Equal("300012345600003", response.Tenant.TaxNumber);
        Assert.Equal("Asia/Riyadh", response.Tenant.TimeZoneId);
        Assert.Equal("me-central-1", response.Tenant.DataRegion);
    }

    [Fact]
    public async Task Malformed_codes_are_refused_with_a_message_naming_the_value()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var plan = await PricedPlanAsync(context);
        var controller = Controller(context);

        var badCountry = Request("bad-country-tenant");
        badCountry.PlanId = plan.Id;
        badCountry.CountryCode = "SAU";
        Assert.Contains("ISO-3166", ErrorOf(await controller.Provision(badCountry, CancellationToken.None)));

        var badCurrency = Request("bad-currency-tenant");
        badCurrency.PlanId = plan.Id;
        badCurrency.BaseCurrencyCode = "RIYAL";
        Assert.Contains("ISO-4217", ErrorOf(await controller.Provision(badCurrency, CancellationToken.None)));

        // A mistyped IANA id would otherwise surface much later as an SLA clock running in the
        // wrong offset, which is a bug nobody traces back to a provisioning form.
        var badZone = Request("bad-zone-tenant");
        badZone.PlanId = plan.Id;
        badZone.TimeZoneId = "Asia/Riyad";
        Assert.Contains("time zone", ErrorOf(await controller.Provision(badZone, CancellationToken.None)));
    }

    [Fact]
    public async Task An_inactive_plan_or_rate_card_cannot_be_attached()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var retired = new Plan
        {
            Code = "legacy-2024", Name = "Legacy", MonthlyPriceUsd = 99m, IsActive = false
        };
        context.Set<Plan>().Add(retired);
        var expiredCard = new ERP_RFQ_Automation.Billing.RateCard
        {
            Code = "legacy-card", Currency = "USD", EffectiveFromUtc = DateTime.UtcNow.AddYears(-3),
            IsActive = false
        };
        context.Set<ERP_RFQ_Automation.Billing.RateCard>().Add(expiredCard);
        await context.SaveChangesAsync();
        var controller = Controller(context);

        var onRetiredPlan = Request("retired-plan-tenant");
        onRetiredPlan.PlanId = retired.Id;
        Assert.Contains("not active", ErrorOf(await controller.Provision(onRetiredPlan, CancellationToken.None)));

        var live = await PricedPlanAsync(context);
        var onRetiredCard = Request("retired-card-tenant");
        onRetiredCard.PlanId = live.Id;
        onRetiredCard.RateCardId = expiredCard.Id;
        Assert.Contains("not active", ErrorOf(await controller.Provision(onRetiredCard, CancellationToken.None)));
    }
}
