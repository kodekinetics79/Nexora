using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class TenantDataRecoveryTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Unknown_provider_state_blocks_certificate_then_immutable_proof_allows_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        await SeedPurgedAsync(context);
        var service = Service(context);

        var blocked = await service.DecisionAsync(41, default);
        Assert.False(blocked.Ready);
        Assert.Contains(blocked.Blockers, x => x.Contains("no post-purge", StringComparison.Ordinal));

        TenantDataRecoveryEvidenceDto? evidence = null;
        foreach (var asset in await context.Set<TenantDataAsset>().OrderBy(x => x.Id).ToListAsync())
        {
            var evidenceType = asset.AssetType is TenantDataAssetTypes.Subprocessor or TenantDataAssetTypes.AiOcrProvider
                ? TenantDataRecoveryEvidenceTypes.SubprocessorDeletionConfirmed
                : TenantDataRecoveryEvidenceTypes.BackupDestructionConfirmed;
            var recorded = await service.RecordAsync(41, Evidence(evidenceType) with
            {
                TenantDataAssetId = asset.Id,
                ScopeKey = asset.LogicalKey,
                OpaqueProviderReference = asset.OpaqueProviderReference,
                CorrelationId = $"correlation:{asset.LogicalKey}",
                IdempotencyKey = $"destroy:{asset.LogicalKey}",
                EvidenceReference = $"evidence:{asset.LogicalKey}"
            }, Actor(), null, default);
            if (asset.AssetType == TenantDataAssetTypes.PostgreSqlTenantScope) evidence = recorded;
        }
        var ready = await service.DecisionAsync(41, default);
        var certificate = await service.CertifyAsync(41,
            new CreateTenantDeletionCertificateRequest("All registered deletion evidence was independently reviewed."),
            Actor(), null, default);

        Assert.True(ready.Ready);
        Assert.Contains(evidence!.Id, ready.EvidenceIds);
        Assert.Equal(64, certificate.EvidenceManifestSha256.Length);
        Assert.Contains(evidence.Id, certificate.EvidenceIds);
        Assert.Equal(TenantDataAssetTypes.All.Count + 1, await context.Set<PlatformAuditLog>().CountAsync());
    }

    [Fact]
    public async Task Active_legal_hold_blocks_backup_destruction_confirmation()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        await SeedPurgedAsync(context);
        context.Set<TenantLegalHold>().Add(new TenantLegalHold
        {
            TenantId = 41, Scope = "AllData", Authority = "Litigation",
            Reason = "Preserve all customer records for active litigation.",
            EvidenceReference = "case:41", PlacedBy = "owner@nexora.test",
            PlacedByPlatformUserId = 7, PlacedOn = Now.AddDays(-1)
        });
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<TenantDataAssetConflictException>(() => Service(context).RecordAsync(
            41, Evidence(TenantDataRecoveryEvidenceTypes.BackupDestructionConfirmed), Actor(), null, default));

        Assert.Contains("legal hold", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Set<TenantDataRecoveryEvidence>().ToListAsync());
    }

    [Fact]
    public async Task Purged_restore_requires_tombstone_reapplication_and_records_actual_rto()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        await SeedPurgedAsync(context);
        var service = Service(context);
        var restore = Evidence(TenantDataRecoveryEvidenceTypes.RestoreDrillCompleted) with
        {
            RecoveryPointUtc = Now.AddHours(-2),
            OperationStartedUtc = Now.AddMinutes(-10),
            CompletedUtc = Now.AddMinutes(-2),
            ConfiguredRpoSeconds = 10_800,
            ConfiguredRtoSeconds = 900,
            CustomerRowsObserved = 0,
            IdempotencyKey = "restore:41:one"
        };

        await Assert.ThrowsAsync<TenantDataAssetConflictException>(() =>
            service.RecordAsync(41, restore, Actor(), null, default));
        await service.RecordAsync(41, Evidence(TenantDataRecoveryEvidenceTypes.TombstoneReapplied) with
        {
            CustomerRowsObserved = 0,
            IdempotencyKey = "tombstone:41:one"
        }, Actor(), null, default);
        var completed = await service.RecordAsync(41, restore, Actor(), null, default);

        Assert.Equal(480, completed.ActualRecoverySeconds);
        Assert.Equal(Now.AddHours(-2), completed.RecoveryPointUtc);
    }

    [Fact]
    public void Recovery_endpoints_are_owner_and_mfa_policy_protected()
    {
        var attribute = Assert.Single(typeof(TenantDataRecoveryController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(PlatformPolicies.Owner, attribute.Policy);
        foreach (var action in new[] { nameof(TenantDataRecoveryController.Record), nameof(TenantDataRecoveryController.Certify) })
        {
            var method = typeof(TenantDataRecoveryController).GetMethod(action)!;
            Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
                x => x.Policy == PlatformPolicies.Mfa);
        }
    }

    private static TenantDataRecoveryService Service(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context) =>
        new(context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            new FixedTimeProvider(Now));

    private static async Task SeedPurgedAsync(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
    {
        context.Set<Tenant>().Add(new Tenant
        {
            Id = 41, Name = "Recovery tenant", Slug = "recovery-tenant", Status = TenantStatus.Archived,
            PrimaryBusinessUnitId = 410, DataRegion = "us-east-1"
        });
        context.Set<TenantOffboarding>().Add(new TenantOffboarding
        {
            TenantId = 41, Stage = TenantOffboardingStage.Purged, PurgedOn = Now.AddHours(-1),
            PurgedBy = "owner@nexora.test", PurgeReason = "Contract ended and retention elapsed.",
            PurgedRowCount = 100, CreatedOn = Now.AddDays(-8)
        });
        context.Set<TenantDataAsset>().Add(new TenantDataAsset
        {
            TenantId = 41, LogicalKey = TenantDataAssetRegistryService.PostgreSqlLogicalKey,
            AssetType = TenantDataAssetTypes.PostgreSqlTenantScope,
            OpaqueProviderReference = "provider:41", Region = "us-east-1",
            Classification = TenantDataAssetClassifications.CustomerData,
            Disposition = TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
            BackupPolicyReference = "policy:standard", BackupPolicyVersion = 1,
            Status = TenantDataAssetStatuses.Verified, Version = 1,
            CreatedOn = Now.AddDays(-10), CreatedBy = "owner@nexora.test"
        });
        foreach (var assetType in TenantDataAssetTypes.All.Where(x => x != TenantDataAssetTypes.PostgreSqlTenantScope))
        {
            context.Set<TenantDataAsset>().Add(new TenantDataAsset
            {
                TenantId = 41, LogicalKey = assetType.ToLowerInvariant(), AssetType = assetType,
                OpaqueProviderReference = $"provider:{assetType.ToLowerInvariant()}", Region = "us-east-1",
                Classification = TenantDataAssetClassifications.CustomerData,
                Disposition = assetType is TenantDataAssetTypes.Subprocessor or TenantDataAssetTypes.AiOcrProvider
                    ? TenantDataAssetDispositions.ProviderDeletionRequired
                    : TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy,
                BackupPolicyReference = "policy:standard", BackupPolicyVersion = 1,
                Status = TenantDataAssetStatuses.Verified, Version = 1,
                CreatedOn = Now.AddDays(-10), CreatedBy = "owner@nexora.test"
            });
        }
        await context.SaveChangesAsync();
    }

    private static RecordTenantDataRecoveryEvidenceRequest Evidence(string type) => new(
        1, TenantDataAssetRegistryService.PostgreSqlLogicalKey, type, "provider:41", "backup:41:one",
        null, null, Now.AddMinutes(-2), null, null, null, 0,
        "evidence:41:one", new string('a', 64), "correlation:41:one", $"{type}:41:one",
        "Independent operator reviewed provider evidence and scope.");

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"), new Claim("email", "owner@nexora.test"),
        new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner)),
        new Claim(PlatformAuthConstants.AuthenticationMethodClaim, PlatformAuthConstants.MfaAuthenticationMethod)
    }, "test"));

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}
