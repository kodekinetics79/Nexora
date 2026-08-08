using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Retention;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class EvidenceRetentionLegalHoldPortableTests
{
    [Fact]
    public async Task Retention_resolves_business_unit_to_platform_tenant_and_detects_active_hold()
    {
        const long businessUnitId = 91_701;
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, businessUnitId);
        db.Set<Tenant>().Add(new Tenant
        {
            Name = "Portable retention hold tenant",
            Slug = "portable-retention-hold",
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = businessUnitId,
            CreatedBy = "test"
        });
        await db.SaveChangesAsync();

        var platformTenantId = await TenantLegalHoldFence.ResolvePlatformTenantIdAsync(
            db, businessUnitId, default);
        Assert.NotNull(platformTenantId);
        Assert.False(await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId.Value, default));

        db.Set<TenantLegalHold>().Add(new TenantLegalHold
        {
            TenantId = platformTenantId.Value,
            Scope = "AllTenantData",
            Authority = "Litigation counsel",
            Reason = "Preserve all tenant evidence for an active litigation matter.",
            EvidenceReference = "case://portable-retention-hold",
            PlacedByPlatformUserId = 17,
            PlacedBy = "legal@nexora.test"
        });
        await db.SaveChangesAsync();

        Assert.True(await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId.Value, default));

        var root = Path.Combine(Path.GetTempPath(), "nexora-portable-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new LocalFileStorage(root, root);
            var retention = new EvidenceRetentionService(db, new LocalEvidenceObjectStorage(files),
                new LegacyAttachmentPurgeResolver(db, files),
                new CommercialDocumentArchiveService(db),
                new NoopLogger<EvidenceRetentionService>());
            var refusal = await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() =>
                retention.AcquireLegalHoldDeletionFenceAsync(businessUnitId, default));
            Assert.Contains("legal hold", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        var hold = await db.Set<TenantLegalHold>().SingleAsync();
        hold.ReleasedOn = DateTime.UtcNow;
        hold.ReleasedByPlatformUserId = 17;
        hold.ReleasedBy = "legal@nexora.test";
        hold.ReleaseReason = "The preservation obligation ended after written counsel approval.";
        await db.SaveChangesAsync();

        Assert.False(await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId.Value, default));
    }
}
