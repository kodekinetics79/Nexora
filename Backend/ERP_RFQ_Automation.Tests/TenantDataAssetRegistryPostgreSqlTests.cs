using System.Security.Claims;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantDataAssetRegistryPostgreSqlTests
{
    private const long TenantId = 983001;
    private const long BusinessUnitId = 983002;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PostgreSql_scope_registration_verification_and_data_gate_use_real_relational_constraints()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var database = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_data_asset_tests")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await database.StartAsync();
        var options = new DbContextOptionsBuilder<ERP_RFQ_Automation.Models.ErpRfqAutomationContext>()
            .UseNpgsql(database.GetConnectionString())
            .Options;
        await using var context = new ERP_RFQ_Automation.Models.ErpRfqAutomationContext(
            options, new StubTenant(null));
        await context.Database.MigrateAsync();
        try
        {
            // The unit the isolation pointer references: PrimaryBusinessUnitId became a real
            // foreign key in 20260907111347, so a tenant can no longer point at a unit that does
            // not exist. BillingContactEmail is set because BillingMode defaults to Billable and
            // 20260907111522 refuses a Billable tenant with no invoice recipient.
            context.Set<ERP_RFQ_Automation.Models.BusinessUnit>().Add(new()
            {
                Id = BusinessUnitId,
                BusinessUnitCode = "PGASSET",
                BusinessUnitName = "PostgreSQL asset unit",
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
            context.Set<Tenant>().Add(new Tenant
            {
                Id = TenantId,
                Name = "PostgreSQL asset tenant",
                Slug = "postgresql-asset-tenant-983001",
                Status = TenantStatus.Provisioning,
                PrimaryBusinessUnitId = BusinessUnitId,
                BillingContactEmail = "ap@postgresql-asset.test",
                DataRegion = "us-east-1"
            });
            await context.SaveChangesAsync();
            var service = new TenantDataAssetRegistryService(context,
                new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance));

            var registered = await service.RegisterAsync(TenantId, new(
                TenantDataAssetRegistryService.PostgreSqlLogicalKey,
                "neon-project-983001:primary",
                "us-east-1",
                TenantDataAssetClassifications.CustomerData,
                TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
                "backup-policy:standard",
                4,
                "Register the real PostgreSQL tenant boundary."), Actor(), null, default);
            var verified = await service.VerifyAsync(TenantId, registered.Id, new(
                registered.Version,
                BusinessUnitId,
                "us-east-1",
                "probe:postgresql-scope:983001:v1",
                new string('b', 64),
                "Owner accepted the hashed PostgreSQL isolation probe."), Actor(), null, default);
            var decision = await service.ActivationDataDecisionAsync(TenantId, default);

            Assert.True(decision.DataGateReady);
            Assert.Equal(2, verified.Version);
            Assert.Equal(1, verified.VerificationVersion);
            Assert.Equal(2, await context.Set<PlatformAuditLog>().CountAsync(x =>
                x.ActAsTenantId == TenantId &&
                (x.Action == TenantDataAssetRegistryService.RegisterAction
                 || x.Action == TenantDataAssetRegistryService.VerifyAction)));

            context.Set<TenantDataAsset>().Add(new TenantDataAsset
            {
                TenantId = TenantId,
                LogicalKey = TenantDataAssetRegistryService.PostgreSqlLogicalKey,
                AssetType = TenantDataAssetTypes.PostgreSqlTenantScope,
                OpaqueProviderReference = "duplicate",
                Region = "us-east-1",
                Classification = TenantDataAssetClassifications.CustomerData,
                Disposition = TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
                BackupPolicyReference = "backup-policy:standard",
                BackupPolicyVersion = 4,
                Status = TenantDataAssetStatuses.Registered,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = "owner@nexora.test"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            context.ChangeTracker.Clear();
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM platform.\"TenantDataAssets\" WHERE \"TenantId\" = {0}", TenantId);
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM platform.\"TenantMeterSourcePolicies\" WHERE \"TenantId\" = {0}", TenantId);
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM platform.\"Tenants\" WHERE \"Id\" = {0}", TenantId);
        }
    }

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"),
        new Claim("email", "owner@nexora.test")
    }, "test"));
}
