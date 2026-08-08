using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantLifecycleReanimationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Restore_waits_for_the_purge_claim_and_then_fails_closed()
    {
        long tenantId;
        await using (var seed = database.ContextFor(null))
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var tenant = new Tenant
            {
                Name = "Lifecycle race tenant",
                Slug = $"lifecycle-race-{suffix}",
                Status = TenantStatus.Archived,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            };
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
            seed.Set<TenantOffboarding>().Add(new TenantOffboarding
            {
                TenantId = tenantId,
                Stage = TenantOffboardingStage.PendingDeletion,
                DeletionScheduledOn = DateTime.UtcNow.AddDays(-31),
                PurgeEligibleOn = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var purgeClaim = database.ContextFor(null);
        await using var claimTransaction = await purgeClaim.Database.BeginTransactionAsync();
        Assert.Equal(1, await purgeClaim.Set<TenantOffboarding>()
            .Where(record => record.TenantId == tenantId
                             && record.Stage == TenantOffboardingStage.PendingDeletion
                             && record.PurgeStartedOn == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                record => record.PurgeStartedOn, DateTime.UtcNow)));

        await using var restoreContext = database.ContextFor(null);
        var restoreTask = Controller(restoreContext).Restore(tenantId,
            new TenantStatusChangeRequest { Reason = "Customer returned during purge" },
            CancellationToken.None);

        await Task.Delay(200);
        Assert.False(restoreTask.IsCompleted,
            "Restore must wait on the same offboarding-row lock used by the purge claim.");

        await claimTransaction.CommitAsync();
        var result = await restoreTask;
        Assert.IsType<ConflictObjectResult>(result.Result);

        await using var verify = database.ContextFor(null);
        Assert.Equal(TenantStatus.Archived,
            (await verify.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId)).Status);
        Assert.NotNull((await verify.Set<TenantOffboarding>()
            .SingleAsync(record => record.TenantId == tenantId)).PurgeStartedOn);
    }

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
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };
    }

    private static ClaimsPrincipal PlatformActor() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "operator@example.test"),
        new Claim("platformRole", "Owner")
    ], "Platform"));
}
