using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

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
