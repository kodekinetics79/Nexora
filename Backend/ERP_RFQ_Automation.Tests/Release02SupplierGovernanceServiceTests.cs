using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.SupplierGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02SupplierGovernanceServiceTests
{
    [Fact]
    public async Task Approved_supplier_requires_verified_cleared_ready_evidence_and_is_audited()
    {
        using var database = new TestDb();
        Guid token;
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            token = Guid.NewGuid();
            setup.Suppliers.Add(Supplier(9001, 501, token));
            await setup.SaveChangesAsync();
        }

        await using var context = database.ContextFor(501);
        var result = await new SupplierGovernanceService(context).GovernAsync(Command(token));

        Assert.Equal(SupplierGovernanceStatuses.Approved, result.GovernanceStatus);
        Assert.Equal(SupplierReadinessStatuses.Ready, result.ReadinessStatus);
        Assert.NotEqual(token, result.ConcurrencyToken);
        var audit = await context.ProcurementEvents.SingleAsync(x =>
            x.EventType == "SUPPLIER_GOVERNANCE_DECIDED" && x.AggregateId == 9001);
        Assert.Contains("evidence reviewed", audit.PayloadJson);
    }

    [Fact]
    public async Task Governance_retry_is_idempotent_and_changed_request_conflicts()
    {
        using var database = new TestDb();
        var token = Guid.NewGuid();
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            setup.Suppliers.Add(Supplier(9001, 501, token));
            await setup.SaveChangesAsync();
        }

        await using (var firstContext = database.ContextFor(501))
            Assert.False((await new SupplierGovernanceService(firstContext).GovernAsync(Command(token))).Replayed);
        await using (var replayContext = database.ContextFor(501))
            Assert.True((await new SupplierGovernanceService(replayContext).GovernAsync(Command(token))).Replayed);
        await using var conflictContext = database.ContextFor(501);
        await Assert.ThrowsAsync<SupplierGovernanceConflictException>(() =>
            new SupplierGovernanceService(conflictContext).GovernAsync(
                Command(token) with { Reason = "different decision" }));
    }

    [Fact]
    public async Task Governance_replay_returns_the_original_decision_after_a_later_change()
    {
        using var database = new TestDb();
        var token = Guid.NewGuid();
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            setup.Suppliers.Add(Supplier(9001, 501, token));
            await setup.SaveChangesAsync();
        }

        GovernedSupplierResult first;
        await using (var firstContext = database.ContextFor(501))
            first = await new SupplierGovernanceService(firstContext).GovernAsync(Command(token));
        await using (var secondContext = database.ContextFor(501))
            await new SupplierGovernanceService(secondContext).GovernAsync(Command(first.ConcurrencyToken) with
            {
                GovernanceStatus = SupplierGovernanceStatuses.Inactive,
                VerificationStatus = SupplierVerificationStatuses.Expired,
                ComplianceStatus = SupplierComplianceStatuses.Pending,
                RiskStatus = SupplierRiskStatuses.Medium,
                ReadinessStatus = SupplierReadinessStatuses.Blocked,
                Reason = "temporarily inactive",
                IdempotencyKey = "supplier-governance-2"
            });
        await using var replayContext = database.ContextFor(501);
        var replay = await new SupplierGovernanceService(replayContext).GovernAsync(Command(token));

        Assert.True(replay.Replayed);
        Assert.Equal(first.GovernanceStatus, replay.GovernanceStatus);
        Assert.Equal(first.ReadinessStatus, replay.ReadinessStatus);
        Assert.Equal(first.ConcurrencyToken, replay.ConcurrencyToken);
        var aggregateVersions = await replayContext.ProcurementEvents
            .Where(x => x.AggregateType == "Supplier" && x.AggregateId == 9001)
            .OrderBy(x => x.AggregateVersion).Select(x => x.AggregateVersion).ToArrayAsync();
        Assert.Equal(new long[] { 1, 2 }, aggregateVersions);
    }

    [Theory]
    [InlineData(SupplierGovernanceStatuses.Inactive, SupplierReadinessStatuses.Ready)]
    [InlineData(SupplierGovernanceStatuses.Approved, SupplierReadinessStatuses.Ready,
        SupplierVerificationStatuses.Verified, SupplierComplianceStatuses.Failed, SupplierRiskStatuses.Low)]
    [InlineData(SupplierGovernanceStatuses.Provisional, SupplierReadinessStatuses.Ready,
        SupplierVerificationStatuses.Verified, SupplierComplianceStatuses.Cleared, SupplierRiskStatuses.High)]
    public async Task Contradictory_governance_states_fail_closed(
        string governanceStatus,
        string readinessStatus,
        string verificationStatus = SupplierVerificationStatuses.Verified,
        string complianceStatus = SupplierComplianceStatuses.Cleared,
        string riskStatus = SupplierRiskStatuses.Low)
    {
        using var database = new TestDb();
        var token = Guid.NewGuid();
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            setup.Suppliers.Add(Supplier(9001, 501, token));
            await setup.SaveChangesAsync();
        }
        await using var context = database.ContextFor(501);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SupplierGovernanceService(context).GovernAsync(Command(token) with
            {
                GovernanceStatus = governanceStatus,
                ReadinessStatus = readinessStatus,
                VerificationStatus = verificationStatus,
                ComplianceStatus = complianceStatus,
                RiskStatus = riskStatus
            }));
    }

    [Fact]
    public async Task Cross_tenant_and_unverified_approval_fail_closed()
    {
        using var database = new TestDb();
        var token = Guid.NewGuid();
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            Seed.EnsureBusinessUnit(setup, 502);
            setup.Suppliers.Add(Supplier(9001, 501, token));
            await setup.SaveChangesAsync();
        }

        await using (var otherTenant = database.ContextFor(502))
            await Assert.ThrowsAsync<SupplierGovernanceNotFoundException>(() =>
                new SupplierGovernanceService(otherTenant).GovernAsync(Command(token) with
                    { BusinessUnitId = 502 }));
        await using var ownTenant = database.ContextFor(501);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new SupplierGovernanceService(ownTenant).GovernAsync(Command(token) with
            {
                VerificationStatus = SupplierGovernanceUnknown.Unknown
            }));
    }

    /// <summary>
    /// THE regression for the bulk-import gap. A Supplier created entirely through the spreadsheet
    /// importer — which never touches <c>SupplierRepository</c> and never sets either governance
    /// identity column — is governable, and the columns are populated.
    ///
    /// <para><b>What used to happen.</b> <c>Supplier.ConcurrencyToken</c> and
    /// <c>Supplier.EffectiveFrom</c> are nullable with no store default and no trigger, and only
    /// <c>SupplierRepository.AddAsync</c> assigned them. A bulk-imported Supplier therefore landed
    /// with both NULL, and <c>GovernAsync</c> compares the stored value against the caller's
    /// NON-nullable <c>ExpectedConcurrencyToken</c>: null can never equal a Guid, so every
    /// governance decision on an imported Supplier was rejected as
    /// <c>SupplierGovernanceConflictException</c> — "The Supplier changed after it was loaded.
    /// Refresh and review the latest evidence." Refreshing returns the same null, so the Supplier
    /// was permanently ungovernable behind an error telling the operator to retry.</para>
    ///
    /// <para>The assertion is the whole journey rather than the column, because the column being
    /// non-null is only interesting if the decision it blocked now goes through.</para>
    /// </summary>
    [Fact]
    public async Task A_supplier_imported_from_a_spreadsheet_can_be_governed()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var database = new TestDb();
        await using (var setup = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(setup, 501);
            await setup.SaveChangesAsync();
        }

        byte[] workbook;
        await using (var context = database.ContextFor(501))
            workbook = await Uploader(context).GenerateTemplateAsync(501);

        await using (var context = database.ContextFor(501))
        {
            using var stream = new MemoryStream(workbook);
            var upload = await Uploader(context).UploadTemplateAsync(stream, 501, "importer@example.test");
            Assert.True(upload.Success, upload.Message);
        }

        long supplierId;
        Guid token;
        await using (var verify = database.ContextFor(501))
        {
            var imported = await verify.Suppliers.AsNoTracking().SingleAsync();
            supplierId = imported.Id;
            Assert.True(imported.ConcurrencyToken.HasValue,
                "A bulk-imported Supplier landed with no concurrency token, which makes every "
                + "governance decision on it fail as a conflict that refreshing cannot clear.");
            token = imported.ConcurrencyToken.Value;

            // Governance is effective from the moment the record came into existence — the same
            // rule SupplierRepository.AddAsync applied when it was the only creation path.
            Assert.Equal(imported.CreatedOn, imported.EffectiveFrom);
        }

        await using var governContext = database.ContextFor(501);
        var decision = await new SupplierGovernanceService(governContext).GovernAsync(new GovernSupplierCommand(
            501, supplierId, SupplierGovernanceStatuses.Approved, SupplierVerificationStatuses.Verified,
            SupplierComplianceStatuses.Cleared, SupplierRiskStatuses.Low, SupplierReadinessStatuses.Ready,
            token, "evidence reviewed", "manager@example.test", "imported-supplier-governance-1", "corr-1"));

        Assert.Equal(SupplierGovernanceStatuses.Approved, decision.GovernanceStatus);
        Assert.NotEqual(token, decision.ConcurrencyToken);
    }

    private static SupplierUploaderService Uploader(ErpRfqAutomationContext context)
        => new(context, NullLogger<SupplierUploaderService>.Instance);

    private static Supplier Supplier(long id, long tenant, Guid token) => new()
    {
        Id = id,
        Buid = tenant,
        Name = "Governed Supplier",
        ContactEmail = "governed@example.test",
        ImageUrl = string.Empty,
        IsActive = true,
        GovernanceStatus = SupplierGovernanceStatuses.Unverified,
        VerificationStatus = SupplierGovernanceUnknown.Unknown,
        ComplianceStatus = SupplierGovernanceUnknown.Unknown,
        RiskStatus = SupplierGovernanceUnknown.Unknown,
        ReadinessStatus = SupplierReadinessStatuses.ReviewRequired,
        ConcurrencyToken = token,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };

    private static GovernSupplierCommand Command(Guid token) => new(
        501, 9001, SupplierGovernanceStatuses.Approved, SupplierVerificationStatuses.Verified,
        SupplierComplianceStatuses.Cleared, SupplierRiskStatuses.Low, SupplierReadinessStatuses.Ready,
        token, "evidence reviewed", "manager@example.test", "supplier-governance-1", "corr-1");
}
