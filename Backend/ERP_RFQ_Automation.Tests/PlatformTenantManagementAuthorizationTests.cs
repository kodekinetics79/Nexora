using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformTenantManagementAuthorizationTests
{
    private sealed class RecordingAudit : IPlatformAuditService
    {
        public List<string> Actions { get; } = [];

        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private static TenantsController Controller(ErpRfqAutomationContext context, IPlatformAuditService audit)
    {
        var controller = new TenantsController(
            context, audit, NullLogger<TenantsController>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new MultiTenancy.TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context), ProvisioningFixture.Invitations(context));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("email", "owner@nexora.app")], "Platform"))
            }
        };
        return controller;
    }

    [Fact]
    public void Tenant_profile_edit_requires_the_tenant_admin_policy()
    {
        var action = typeof(TenantsController).GetMethod(nameof(TenantsController.UpdateProfile))!;
        var authorization = Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));

        Assert.Equal(PlatformPolicies.TenantAdmin, authorization.Policy);
    }

    [Fact]
    public async Task Profile_edit_persists_the_customer_identity_and_audit_occurrence()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var tenant = new Tenant
        {
            Name = "Acme", Slug = "acme", Status = TenantStatus.Provisioning,
            CountryCode = "US", CreatedOn = DateTime.UtcNow, CreatedBy = "tests"
        };
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();
        var audit = new RecordingAudit();

        var response = await Controller(context, audit).UpdateProfile(tenant.Id,
            new UpdateTenantProfileRequest
            {
                Name = "Acme Aerospace", LegalName = "Acme Aerospace LLC", CountryCode = "sa",
                Industry = "Aerospace", ContactEmail = "admin@acme.test",
                TimeZoneId = "Asia/Riyadh", Locale = "en-SA", Reason = "Customer identity correction"
            }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var stored = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        Assert.Equal("Acme Aerospace", stored.Name);
        Assert.Equal("Acme Aerospace LLC", stored.LegalName);
        Assert.Equal("SA", stored.CountryCode);
        Assert.Equal("Aerospace", stored.Industry);
        Assert.Equal("owner@nexora.app", stored.ModifiedBy);
        Assert.Contains("tenant.profile.update", audit.Actions);
    }
}
