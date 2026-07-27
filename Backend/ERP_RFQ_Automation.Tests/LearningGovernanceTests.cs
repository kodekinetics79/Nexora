using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LearningGovernanceTests
{
    [Fact]
    public void Signal_identity_is_deterministic_and_normalized()
    {
        var first = LearningGovernanceRules.SupplierQuoteCorrectionSignalId(
            " Lead Time Days ", " two weeks ");
        var second = LearningGovernanceRules.SupplierQuoteCorrectionSignalId(
            "lead   time days", "TWO WEEKS");
        var differentAlias = LearningGovernanceRules.SupplierQuoteCorrectionSignalId(
            "Lead Time Days", "four weeks");

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentAlias);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Rollback_resolves_the_compensated_state_without_rewriting_history()
    {
        var history = new[]
        {
            Event(1, LearningGovernanceActions.Approved),
            Event(2, LearningGovernanceActions.Disabled),
            Event(3, LearningGovernanceActions.RolledBack, 2)
        };

        Assert.Equal("APPROVED", LearningGovernanceRules.EffectiveStatus(history, "OBSERVED"));
        Assert.Equal(3, history.Length);
    }

    [Fact]
    public void Learning_governance_mutations_require_dashboard_edit()
    {
        AssertPermission(nameof(CommercialLearningController.ApproveLearningSignal), PermissionAction.Edit);
        AssertPermission(nameof(CommercialLearningController.DisableLearningSignal), PermissionAction.Edit);
        AssertPermission(nameof(CommercialLearningController.RollbackLearningSignal), PermissionAction.Edit);
    }

    [Fact]
    public async Task Governance_is_append_only_versioned_replay_safe_and_tenant_qualified()
    {
        using var fixture = new ProcurementScenario();
        var signalId = await SeedSignalAsync(fixture);

        await using (var context = fixture.Context())
        {
            var service = new LearningGovernanceService(context);
            var approved = await service.GovernAsync(fixture.BusinessUnitId, signalId,
                LearningGovernanceActions.Approved, new(0, "Validated correction evidence"),
                7001, "learning-approve-1");
            var replay = await service.GovernAsync(fixture.BusinessUnitId, signalId,
                LearningGovernanceActions.Approved, new(0, "Validated correction evidence"),
                7001, "learning-approve-1");
            var disabled = await service.GovernAsync(fixture.BusinessUnitId, signalId,
                LearningGovernanceActions.Disabled, new(1, "Drift detected in current format"),
                7002, "learning-disable-2");

            Assert.Equal("APPROVED", approved.EffectiveStatus);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(approved.OccurredOn, replay.OccurredOn);
            Assert.Equal("DISABLED", disabled.EffectiveStatus);
            await Assert.ThrowsAsync<LearningGovernanceConflictException>(() => service.GovernAsync(
                fixture.BusinessUnitId, signalId, LearningGovernanceActions.Approved,
                new(1, "Stale decision"), 7003, "learning-stale"));
            await Assert.ThrowsAsync<LearningGovernanceConflictException>(() => service.GovernAsync(
                fixture.BusinessUnitId, signalId, LearningGovernanceActions.Disabled,
                new(0, "Different replay"), 7001, "learning-approve-1"));

            var rolledBack = await service.GovernAsync(fixture.BusinessUnitId, signalId,
                LearningGovernanceActions.RolledBack,
                new(2, "Disable decision was not supported", 2), 7003, "learning-rollback-3");
            var rollbackReplay = await service.GovernAsync(fixture.BusinessUnitId, signalId,
                LearningGovernanceActions.RolledBack,
                new(2, "Disable decision was not supported", 2), 7003, "learning-rollback-3");
            Assert.Equal("APPROVED", rolledBack.EffectiveStatus);
            Assert.Equal("APPROVED", rollbackReplay.EffectiveStatus);
            Assert.Equal(3, await context.LearningGovernanceEvents.CountAsync());

            var studioSignal = Assert.Single(await service.BuildSignalsAsync(fixture.BusinessUnitId));
            Assert.Equal("APPROVED", studioSignal.Status);
            Assert.Equal(3, studioSignal.GovernanceVersion);
            Assert.Equal(LearningGovernanceActions.RolledBack, studioSignal.GovernanceAction);
        }

        await using var otherTenant = fixture.Context(fixture.OtherBusinessUnitId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new LearningGovernanceService(otherTenant)
            .GovernAsync(fixture.OtherBusinessUnitId, signalId, LearningGovernanceActions.Approved,
                new(0, "Forged tenant decision"), 8001, "other-tenant-key"));
    }

    [Fact]
    public async Task Governed_signal_remains_visible_after_it_leaves_the_derived_window()
    {
        using var fixture = new ProcurementScenario();
        var signalId = await SeedSignalAsync(fixture);
        await using var context = fixture.Context();
        var service = new LearningGovernanceService(context);
        await service.GovernAsync(fixture.BusinessUnitId, signalId,
            LearningGovernanceActions.Approved, new(0, "Approved durable correction"),
            7001, "learning-durable-approve");

        context.SupplierQuoteReviewDecisions.RemoveRange(
            await context.SupplierQuoteReviewDecisions.ToListAsync());
        await context.SaveChangesAsync();

        var durable = Assert.Single(await service.BuildSignalsAsync(fixture.BusinessUnitId));
        Assert.Equal(signalId, durable.SignalId);
        Assert.Equal("APPROVED", durable.Status);
        Assert.Equal(1, durable.GovernanceVersion);
    }

    private static LearningGovernanceEvent Event(long version, string action, long? revertsVersion = null) => new()
    {
        Version = version,
        Action = action,
        RevertsVersion = revertsVersion
    };

    private static void AssertPermission(string methodName, PermissionAction action)
    {
        var permission = Assert.Single(typeof(CommercialLearningController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>());
        Assert.Equal("Dashboard", permission.ModuleName);
        Assert.Equal(action, permission.Action);
    }

    private static async Task<string> SeedSignalAsync(ProcurementScenario fixture)
    {
        await using (var setup = fixture.Context())
        {
            var rfq = await setup.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
            setup.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-LEARNING";
            var product = await setup.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
            product.PreferredSupplierId = ProcurementTestData.Supplier;
            var line = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            line.ManufacturerPartNumber = "QA-PART-LEARNING";
            await setup.SaveChangesAsync();
        }

        var sourcingCase = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(new(
            fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false,
            "learning-case", "qa", "corr-learning-case")));
        var candidate = Assert.Single(sourcingCase.Candidates);
        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(new(
            fixture.BusinessUnitId, sourcingCase.Id, candidate.SupplierId, DateTime.UtcNow.AddDays(2),
            sourcingCase.Version, "learning-prepare", "qa", "corr-learning-prepare")));

        await using var context = fixture.Context();
        const long quoteId = 991_001;
        const long revisionId = 991_002;
        const long evidenceId = 991_003;
        var now = DateTime.UtcNow;
        context.SupplierQuotes.Add(new SupplierQuote
        {
            Id = quoteId,
            BusinessUnitId = fixture.BusinessUnitId,
            SupplierId = candidate.SupplierId,
            SupplierSolicitationId = prepared.SupplierSolicitationId,
            SourcingCaseId = sourcingCase.Id,
            RfqId = fixture.RfqId,
            NexoraSerial = sourcingCase.NexoraSerial,
            SupplierQuoteReference = "SQ-LEARNING",
            CurrentRevisionNumber = 1,
            InboxStatus = SupplierQuoteInboxStatuses.ReviewRequired,
            Version = 1,
            CreatedOn = now,
            CreatedBy = "qa",
            UpdatedOn = now,
            UpdatedBy = "qa"
        });
        context.SupplierQuoteRevisions.Add(new SupplierQuoteRevision
        {
            Id = revisionId,
            BusinessUnitId = fixture.BusinessUnitId,
            SupplierQuoteId = quoteId,
            RevisionNumber = 1,
            CaptureChannel = SupplierQuoteCaptureChannels.Upload,
            SourceIdentity = "learning.pdf",
            SourceSha256 = new string('a', 64),
            CurrencyId = ProcurementTestData.Currency,
            FreightAmount = 0,
            TaxAmount = 0,
            RequiresReview = true,
            IdempotencyKey = "learning-revision",
            RequestHash = new string('b', 64),
            CapturedOn = now,
            CapturedBy = "qa",
            CorrelationId = "corr-learning-revision"
        });
        context.SupplierQuoteFieldEvidence.Add(new SupplierQuoteFieldEvidence
        {
            Id = evidenceId,
            BusinessUnitId = fixture.BusinessUnitId,
            SupplierQuoteRevisionId = revisionId,
            FieldName = "Lead Time Days",
            OriginalValue = "two weeks",
            NormalizedValue = "14",
            Confidence = .6m,
            Method = "LOCAL_RULE",
            Critical = true,
            ReviewRequired = true,
            CreatedOn = now
        });
        context.SupplierQuoteReviewDecisions.Add(new SupplierQuoteReviewDecision
        {
            Id = 991_004,
            BusinessUnitId = fixture.BusinessUnitId,
            SupplierQuoteRevisionId = revisionId,
            SupplierQuoteFieldEvidenceId = evidenceId,
            Status = SupplierQuoteReviewStatuses.Corrected,
            CorrectedValue = "14",
            Reason = "Confirmed against source evidence",
            ReviewedBy = "qa",
            ReviewedOn = now,
            CorrelationId = "corr-learning-review"
        });
        await context.SaveChangesAsync();
        return LearningGovernanceRules.SupplierQuoteCorrectionSignalId("Lead Time Days", "two weeks");
    }
}
