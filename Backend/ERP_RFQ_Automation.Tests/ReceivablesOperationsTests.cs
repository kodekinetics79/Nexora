using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ReceivablesOperationsTests
{
    private const string ContactVerificationSecret = "test-contact-verification-secret-32-bytes";
    private const string DunningProviderSecret = "test-dunning-provider-secret-at-least-32-bytes";
    [Fact]
    public void Controller_EveryActionUsesItsDedicatedModulePermission()
    {
        var expected = new Dictionary<string, (string Module, PermissionAction Action)>
        {
            [nameof(ReceivablesOperationsController.CreateContact)] = ("Collection Controls", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.DeactivateContact)] = ("Collection Controls", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetContacts)] = ("Collection Controls", PermissionAction.View),
            [nameof(ReceivablesOperationsController.CreateStatement)] = ("Customer Statements", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.FinalizeStatement)] = ("Customer Statements", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.CancelStatement)] = ("Customer Statements", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetStatement)] = ("Customer Statements", PermissionAction.View),
            [nameof(ReceivablesOperationsController.GetStatementArtifact)] = ("Customer Statements", PermissionAction.View),
            [nameof(ReceivablesOperationsController.GetStatements)] = ("Customer Statements", PermissionAction.View),
            [nameof(ReceivablesOperationsController.CreatePolicy)] = ("Dunning Policies", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.ApprovePolicy)] = ("Dunning Policies", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.ActivatePolicy)] = ("Dunning Policies", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.RetirePolicy)] = ("Dunning Policies", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetPolicies)] = ("Dunning Policies", PermissionAction.View),
            [nameof(ReceivablesOperationsController.UpsertCollectionProfile)] = ("Dunning Policies", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetCollectionProfiles)] = ("Dunning Policies", PermissionAction.View),
            [nameof(ReceivablesOperationsController.CreateControl)] = ("Collection Controls", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.ResolveControl)] = ("Collection Controls", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetControls)] = ("Collection Controls", PermissionAction.View),
            [nameof(ReceivablesOperationsController.OpenCase)] = ("Dunning Cases", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.TransitionCase)] = ("Dunning Cases", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.AssignCase)] = ("Dunning Cases", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.CreatePromise)] = ("Dunning Cases", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.ClosePromise)] = ("Dunning Cases", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetCases)] = ("Dunning Cases", PermissionAction.View),
            [nameof(ReceivablesOperationsController.CreateNotice)] = ("Dunning Notices", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.TransitionNotice)] = ("Dunning Notices", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.RecordDeliveryResult)] = ("Dunning Notices", PermissionAction.Edit),
            [nameof(ReceivablesOperationsController.GetNotices)] = ("Dunning Notices", PermissionAction.View),
            [nameof(ReceivablesOperationsController.RunDunning)] = ("Dunning Notices", PermissionAction.Create),
            [nameof(ReceivablesOperationsController.GetRuns)] = ("Dunning Notices", PermissionAction.View)
        };
        var actions = typeof(ReceivablesOperationsController).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(Task<IActionResult>))
            .ToArray();

        Assert.Equal(expected.Keys.Order(), actions.Select(action => action.Name).Order());
        foreach (var action in actions)
        {
            var permission = Assert.Single(action.GetCustomAttributes<RequireModulePermissionAttribute>(true));
            Assert.Equal(expected[action.Name].Module, permission.ModuleName);
            Assert.Equal(expected[action.Name].Action, permission.Action);
        }
    }

    [Fact]
    public void Controller_TenantResolutionUsesOnlyTheAuthenticatedClaim()
    {
        var controller = ControllerWithClaims(new Claim("businessUnitId", BusinessUnitId.ToString()));
        controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?businessUnitId=999999");

        Assert.Equal(BusinessUnitId, InvokeTenantId(controller));

        var missingClaim = ControllerWithClaims(new Claim(ClaimTypes.NameIdentifier, "actor-1"));
        var exception = Assert.Throws<TargetInvocationException>(() => InvokeTenantId(missingClaim));
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public async Task Contact_RequiresProviderEventAndReplaysOnlyAnIdenticalRequest()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var effectiveFrom = DateTime.UtcNow.AddMinutes(-5);
        var request = SignContactRequest(new CreateFinanceCommunicationContactRequest(
            CustomerId, "Collections", "Email", "token:verified_contact_123",
            "r***@example.com", effectiveFrom, null, "provider-evidence-123", Guid.NewGuid(), string.Empty));

        var invalid = request with { VerificationProviderEventId = Guid.Empty };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateContactAsync(BusinessUnitId, "contact-empty-provider", invalid, "maker-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateContactAsync(
            BusinessUnitId, "contact-bad-signature", request with { ProviderSignature = new string('0', 64) }, "maker-1"));

        var created = await service.CreateContactAsync(BusinessUnitId, "contact-replay-1", request, "maker-1");
        var replay = await service.CreateContactAsync(BusinessUnitId, "contact-replay-1", request, "maker-1");

        Assert.Equal(created.Id, replay.Id);
        Assert.Single(await db.FinanceCommunicationContacts.ToListAsync());
        var changed = SignContactRequest(request with { MaskedDestination = "changed@example.com" });
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateContactAsync(
            BusinessUnitId, "contact-replay-1", changed, "maker-1"));
    }

    [Fact]
    public async Task PolicyLifecycle_RequiresIndependentMakerCheckerAndActivator()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        var service = CreateService(db);
        var request = new CreateDunningPolicyRequest(
            "Standard collections", "AE", "UTC", 3, 7, 25m, 21, 8, "policy-v1",
            [new DunningPolicyStepRequest(1, 1, 25m, 0, "Email", "notice-v1", true, "Collector", 3)]);

        var draft = await service.CreatePolicyAsync(BusinessUnitId, "policy-lifecycle-1", request, "maker-1");
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ApprovePolicyAsync(
            BusinessUnitId, draft.Id, new DunningPolicyActionRequest(draft.Version), "maker-1"));

        var approved = await service.ApprovePolicyAsync(
            BusinessUnitId, draft.Id, new DunningPolicyActionRequest(draft.Version), "checker-1");
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ActivatePolicyAsync(
            BusinessUnitId, approved.Id, new DunningPolicyActionRequest(approved.Version), "checker-1"));

        var active = await service.ActivatePolicyAsync(
            BusinessUnitId, approved.Id, new DunningPolicyActionRequest(approved.Version), "operator-1");

        Assert.Equal("Active", active.Status);
        Assert.Equal("checker-1", active.ApprovedBy);
        Assert.Equal(draft.Version + 2, active.Version);
    }

    [Fact]
    public async Task CollectionControl_ReplaysOnlyAnIdenticalRequest()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var request = new CreateCollectionControlRequest(
            CustomerId, null, null, CollectionControlTypes.LegalHold, null,
            "LEGAL_REVIEW", "Approved legal hold pending counsel review",
            "legal-case-evidence-123", DateTime.UtcNow.AddMinutes(-5), null, null);

        var created = await service.CreateControlAsync(BusinessUnitId, "control-replay-1", request, "maker-1");
        var replay = await service.CreateControlAsync(BusinessUnitId, "control-replay-1", request, "maker-1");

        Assert.Equal(created.Id, replay.Id);
        Assert.Single(await db.CollectionControls.ToListAsync());
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateControlAsync(
            BusinessUnitId, "control-replay-1", request with { ReasonCode = "LEGAL_ESCALATED" }, "maker-1"));
    }

    [Fact]
    public async Task Statement_PersistsExactArtifactHashAndCorrectionRequiresReason()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var request = new CreateCustomerStatementRequest(
            CustomerId, null, cutoff.AddDays(-30), cutoff, null, "statement-v1");

        var draft = await service.CreateStatementAsync(BusinessUnitId, "statement-artifact-1", request, "maker-1");
        var replay = await service.CreateStatementAsync(BusinessUnitId, "statement-artifact-1", request, "maker-1");
        var stored = await db.CustomerStatements.SingleAsync(statement => statement.Id == draft.Id);
        var computedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored.ArtifactContent)))
            .ToLowerInvariant();

        Assert.Equal(draft.Id, replay.Id);
        Assert.Equal(computedHash, stored.ArtifactHash);
        Assert.Equal(stored.ArtifactHash, draft.ArtifactHash);
        Assert.Equal("text/html; charset=utf-8", stored.ArtifactMediaType);
        Assert.Contains("<h1>Customer statement</h1>", stored.ArtifactContent, StringComparison.Ordinal);
        Assert.Contains("Receivables Test Customer", stored.ArtifactContent, StringComparison.Ordinal);
        Assert.Null(await service.GetStatementArtifactAsync(BusinessUnitId, draft.Id));

        var finalized = await service.FinalizeStatementAsync(
            BusinessUnitId, draft.Id, new StatementActionRequest(draft.Version), "checker-1");
        var artifact = await service.GetStatementArtifactAsync(BusinessUnitId, draft.Id);
        Assert.NotNull(artifact);
        Assert.NotNull(finalized.StatementNumber);
        Assert.Contains(finalized.StatementNumber, artifact.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{STATEMENT_NUMBER}}", artifact.Content, StringComparison.Ordinal);
        var finalizedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(artifact.Content)))
            .ToLowerInvariant();
        Assert.Equal(finalizedHash, artifact.ArtifactHash);
        Assert.Equal($"statement:{draft.Id}:{finalizedHash}", finalized.ArtifactReference);

        var correction = request with
        {
            SupersedesStatementId = finalized.Id,
            CorrectionReason = null
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateStatementAsync(
            BusinessUnitId, "statement-correction-no-reason", correction, "maker-2"));
    }

    [Fact]
    public async Task Statement_HistoricalSnapshotUsesEffectiveReversalDatesForEveryLedgerSource()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var cutoff = DateTime.UtcNow.AddDays(-5);
        var invoice = AddInvoice(db, 200m, cutoff.AddDays(-30), cutoff.AddDays(-20),
            status: ReceivableDocumentStatuses.Void, voidedOn: cutoff.AddDays(2));
        var payment = AddPayment(db, 80m, cutoff.AddDays(-15),
            status: CustomerPaymentStatuses.Reversed, reversedOn: cutoff.AddDays(2));
        await db.SaveChangesAsync();
        AddPaymentAllocation(db, payment, invoice, 80m);
        AddWriteOff(db, invoice, 20m, cutoff.AddDays(-10),
            FinanceExceptionStatuses.Reversed, cutoff.AddDays(2));
        AddRefund(db, payment, 30m, cutoff.AddDays(-2),
            FinanceExceptionStatuses.Reversed, cutoff.AddDays(2));
        await db.SaveChangesAsync();

        var statement = await CreateService(db).CreateStatementAsync(
            BusinessUnitId, "historical-all-sources", new CreateCustomerStatementRequest(
                CustomerId, null, cutoff.AddDays(-60), cutoff, null, "statement-v1"), "maker-1");

        Assert.Equal(230m, statement.DebitTotal);
        Assert.Equal(100m, statement.CreditTotal);
        Assert.Equal(130m, statement.ClosingBalance);
        Assert.Equal(statement.ClosingBalance, statement.NetCustomerPosition);
        Assert.Equal(100m, AgingTotal(statement));
        Assert.Contains(statement.Lines, x => x.SourceType == ReceivableDocumentTypes.Invoice && x.DebitAmount == 200m);
        Assert.Contains(statement.Lines, x => x.SourceType == "Payment" && x.CreditAmount == 80m);
        Assert.Contains(statement.Lines, x => x.SourceType == "WriteOff" && x.CreditAmount == 20m);
        Assert.Contains(statement.Lines, x => x.SourceType == "Refund" && x.DebitAmount == 30m);
    }

    [Fact]
    public async Task Statement_WriteOffIsCreditAndClosingBalanceReconcilesToAging()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var invoice = AddInvoice(db, 100m, cutoff.AddDays(-45), cutoff.AddDays(-30));
        await db.SaveChangesAsync();
        AddWriteOff(db, invoice, 40m, cutoff.AddDays(-10), FinanceExceptionStatuses.Posted);
        await db.SaveChangesAsync();

        var statement = await CreateService(db).CreateStatementAsync(
            BusinessUnitId, "writeoff-credit", new CreateCustomerStatementRequest(
                CustomerId, null, cutoff.AddDays(-60), cutoff, null, "statement-v1"), "maker-1");

        var writeOffLine = Assert.Single(statement.Lines, x => x.SourceType == "WriteOff");
        Assert.Equal(0m, writeOffLine.DebitAmount);
        Assert.Equal(40m, writeOffLine.CreditAmount);
        Assert.Equal(100m, statement.DebitTotal);
        Assert.Equal(40m, statement.CreditTotal);
        Assert.Equal(60m, statement.ClosingBalance);
        Assert.Equal(statement.ClosingBalance, AgingTotal(statement));
    }

    [Fact]
    public async Task DocumentSpecificDisputeBlocksAggregateDunningCase()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var invoice = AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        var statement = await CreateFinalizedStatementAsync(service, "document-control-statement");
        var policy = await CreateActivePolicyAsync(service, "document-control-policy");
        await AddProfileAsync(service, policy.Id);
        await service.CreateControlAsync(BusinessUnitId, "document-dispute", new CreateCollectionControlRequest(
            CustomerId, null, invoice.Id, CollectionControlTypes.Dispute, 25m, "INVOICE_DISPUTE",
            "Customer disputes a governed invoice line item", "case:dispute:invoice-1",
            DateTime.UtcNow.AddMinutes(-5), null, null), "control-maker");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.OpenCaseAsync(
            BusinessUnitId, "blocked-document-case", new OpenDunningCaseRequest(statement.Id, policy.Id, null),
            "collector-1"));
        Assert.Empty(await db.DunningCases.ToListAsync());
    }

    [Fact]
    public async Task NonOverdueBalanceCannotOpenCaseOrCreateNotice()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10));
        await db.SaveChangesAsync();
        var statement = await CreateFinalizedStatementAsync(service, "current-balance-statement");
        var policy = await CreateActivePolicyAsync(service, "current-balance-policy");
        var contact = await CreateCollectionsContactAsync(service, "current-balance-contact");
        await AddProfileAsync(service, policy.Id, contact.Id);

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.OpenCaseAsync(
            BusinessUnitId, "current-balance-case", new OpenDunningCaseRequest(statement.Id, policy.Id, null),
            "collector-1"));

        var impossibleCase = AddCase(db, statement.Id, policy.Id, 100m, DateTime.UtcNow.AddDays(10));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateNoticeAsync(
            BusinessUnitId, "current-balance-notice", new CreateDunningNoticeRequest(impossibleCase.Id, contact.Id),
            "notice-maker"));
        Assert.Empty(await db.DunningNotices.ToListAsync());
    }

    [Fact]
    public async Task PromiseRefreshesStaleCaseExposureBeforeAcceptingAmount()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var invoice = AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        var statement = await CreateFinalizedStatementAsync(service, "stale-promise-statement");
        var policy = await CreateActivePolicyAsync(service, "stale-promise-policy");
        var item = AddCase(db, statement.Id, policy.Id, 100m, DateTime.UtcNow.AddDays(-30));
        var payment = AddPayment(db, 50m, DateTime.UtcNow.AddMinutes(-1));
        await db.SaveChangesAsync();
        AddPaymentAllocation(db, payment, invoice, 50m);
        await db.SaveChangesAsync();

        var promise = await service.CreatePromiseAsync(
            BusinessUnitId, item.Id, "stale-promise", new CreatePromiseToPayRequest(
                item.Version, 25m, DateTime.UtcNow.Date.AddDays(3), "customer-email-evidence"), "collector-1");

        await db.Entry(item).ReloadAsync();
        Assert.Equal(50m, item.CurrentExposure);
        Assert.Equal(25m, promise.Amount);
        Assert.Single(await db.PromisesToPay.ToListAsync());
    }

    [Fact]
    public async Task KeptPromiseRequiresAndPersistsUniqueEligiblePostedPayment()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        var statement = await CreateFinalizedStatementAsync(service, "kept-promise-statement");
        var policy = await CreateActivePolicyAsync(service, "kept-promise-policy");
        var item = AddCase(db, statement.Id, policy.Id, 100m, DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        var promise = await service.CreatePromiseAsync(BusinessUnitId, item.Id, "promise-one",
            new CreatePromiseToPayRequest(item.Version, 50m, DateTime.UtcNow.Date.AddDays(3),
                "customer-promise-evidence"), "collector-1");

        await Assert.ThrowsAsync<ArgumentException>(() => service.ClosePromiseAsync(
            BusinessUnitId, promise.Id, new ClosePromiseToPayRequest(
                promise.Version, "Kept", "payment-match-evidence"), "collector-2"));

        var payment = AddPayment(db, 50m, DateTime.UtcNow);
        await db.SaveChangesAsync();
        var refund = AddRefund(db, payment, 10m, DateTime.UtcNow.AddMinutes(-1), "Released");
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ClosePromiseAsync(
            BusinessUnitId, promise.Id, new ClosePromiseToPayRequest(
                promise.Version, "Kept", "net-payment-match-evidence", payment.Id), "collector-2"));
        refund.Status = FinanceExceptionStatuses.Reversed;
        refund.PostingStatus = "Reversed";
        refund.ReversedBy = "refund-reverser";
        refund.ReversedOn = DateTime.UtcNow.AddSeconds(-1);
        refund.ReversalReason = "Refund was reversed before promise settlement";
        refund.ReversalEvidenceReference = "case:refund:promise-reversal";
        await db.SaveChangesAsync();
        var kept = await service.ClosePromiseAsync(BusinessUnitId, promise.Id, new ClosePromiseToPayRequest(
            promise.Version, "Kept", "payment-match-evidence", payment.Id), "collector-2");
        Assert.Equal(payment.Id, kept.MatchedPaymentId);
        Assert.Equal(50m, kept.MatchedAmount);
        var stored = await db.PromisesToPay.SingleAsync(x => x.Id == promise.Id);
        Assert.Equal(payment.Id, stored.MatchedPaymentId);
        Assert.Equal(50m, stored.MatchedAmount);

        var caseVersion = await db.DunningCases.Where(x => x.Id == item.Id).Select(x => x.Version).SingleAsync();
        var second = await service.CreatePromiseAsync(BusinessUnitId, item.Id, "promise-two",
            new CreatePromiseToPayRequest(caseVersion, 25m, DateTime.UtcNow.Date.AddDays(4),
                "second-promise-evidence"), "collector-1");
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ClosePromiseAsync(
            BusinessUnitId, second.Id, new ClosePromiseToPayRequest(
                second.Version, "Kept", "duplicate-payment-evidence", payment.Id), "collector-2"));
    }

    [Fact]
    public async Task NoticeArtifactRemainsImmutableAndDeliveryAttemptUsesArtifactHash()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        var statement = await CreateFinalizedStatementAsync(service, "notice-artifact-statement");
        var policy = await CreateActivePolicyAsync(service, "notice-artifact-policy", requiresApproval: true);
        var contact = await CreateCollectionsContactAsync(service, "notice-artifact-contact");
        await AddProfileAsync(service, policy.Id, contact.Id, "en-US");
        var item = await service.OpenCaseAsync(BusinessUnitId, "notice-artifact-case",
            new OpenDunningCaseRequest(statement.Id, policy.Id, "collector-1"), "collector-1");

        var draft = await service.CreateNoticeAsync(BusinessUnitId, "notice-artifact",
            new CreateDunningNoticeRequest(item.Id, contact.Id), "notice-maker");
        Assert.Equal("en-US", draft.Locale);
        Assert.False(string.IsNullOrWhiteSpace(draft.Subject));
        Assert.Equal("text/plain; charset=utf-8", draft.ArtifactMediaType);
        Assert.False(string.IsNullOrWhiteSpace(draft.ArtifactContent));
        var canonicalArtifact = string.Join('\n',
            draft.Subject, draft.ArtifactMediaType, draft.Locale, draft.ArtifactContent);
        var computed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalArtifact)))
            .ToLowerInvariant();
        Assert.Equal(computed, draft.ArtifactHash);

        var approved = await service.TransitionNoticeAsync(BusinessUnitId, draft.Id, "approve",
            new DunningNoticeActionRequest(draft.Version), "notice-checker");
        var released = await service.TransitionNoticeAsync(BusinessUnitId, draft.Id, "release",
            new DunningNoticeActionRequest(approved.Version), "notice-releaser");
        var deliveryRequest = new DunningDeliveryResultRequest(
            released.Version, Guid.NewGuid(), "provider-message-1", DateTime.UtcNow, "signed-provider-evidence");
        deliveryRequest = deliveryRequest with
        {
            ProviderSignature = ProviderSignature(DunningProviderSecret, draft.Id, true, deliveryRequest)
        };
        var delivered = await service.RecordDeliveryResultAsync(
            BusinessUnitId, draft.Id, true, deliveryRequest, "provider-webhook");

        Assert.Equal(draft.Locale, delivered.Locale);
        Assert.Equal(draft.Subject, delivered.Subject);
        Assert.Equal(draft.ArtifactMediaType, delivered.ArtifactMediaType);
        Assert.Equal(draft.ArtifactContent, delivered.ArtifactContent);
        Assert.Equal(draft.ArtifactHash, delivered.ArtifactHash);
        Assert.Equal(draft.ArtifactHash, Assert.Single(delivered.DeliveryAttempts).ArtifactHash);
    }

    [Fact]
    public async Task CancelledCorrectionCanBeReplaced()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var request = new CreateCustomerStatementRequest(
            CustomerId, null, cutoff.AddDays(-30), cutoff, null, "statement-v1");
        var originalDraft = await service.CreateStatementAsync(
            BusinessUnitId, "correction-original", request, "maker-1");
        var original = await service.FinalizeStatementAsync(BusinessUnitId, originalDraft.Id,
            new StatementActionRequest(originalDraft.Version), "checker-1");
        var correctionRequest = request with
        {
            SupersedesStatementId = original.Id,
            CorrectionReason = "Correcting an identified customer statement defect"
        };
        var cancelledDraft = await service.CreateStatementAsync(
            BusinessUnitId, "correction-cancelled", correctionRequest, "maker-2");
        await service.CancelStatementAsync(BusinessUnitId, cancelledDraft.Id,
            new StatementActionRequest(cancelledDraft.Version, "Correction draft withdrawn after review"), "maker-2");

        var replacement = await service.CreateStatementAsync(
            BusinessUnitId, "correction-replacement", correctionRequest, "maker-3");

        Assert.Equal(2, replacement.Revision);
        Assert.Equal(original.Id, replacement.SupersedesStatementId);
        Assert.Equal(CustomerStatementStatuses.Draft, replacement.Status);
        var successors = await db.CustomerStatements.Where(x => x.SupersedesStatementId == original.Id).ToListAsync();
        Assert.Equal(2, successors.Count);
        Assert.Contains(successors, x => x.Status == CustomerStatementStatuses.Cancelled);
        Assert.Contains(successors, x => x.Status == CustomerStatementStatuses.Draft);
    }

    [Fact]
    public async Task DeliveryWebhookRejectsMissingAndInvalidHmacButAcceptsValidSignature()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CommercialFinance:DunningProviderWebhookSecret"] = secret
        }).Build();
        var proxy = DispatchProxy.Create<IReceivablesOperationsService, RecordingReceivablesServiceProxy>();
        var recorder = (RecordingReceivablesServiceProxy)(object)proxy;
        var request = new DunningDeliveryResultRequest(
            3, Guid.NewGuid(), "provider-message-1", DateTime.UtcNow, "signed-provider-evidence");

        var missing = ControllerWithClaims(proxy, configuration,
            new Claim("businessUnitId", BusinessUnitId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "provider-webhook"));
        Assert.IsType<BadRequestObjectResult>(await missing.RecordDeliveryResult(42, true, request));

        var invalid = ControllerWithClaims(proxy, configuration,
            new Claim("businessUnitId", BusinessUnitId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "provider-webhook"));
        invalid.Request.Headers["X-Nexora-Provider-Signature"] = "not-a-valid-hex-signature";
        Assert.IsType<BadRequestObjectResult>(await invalid.RecordDeliveryResult(42, true, request));

        var valid = ControllerWithClaims(proxy, configuration,
            new Claim("businessUnitId", BusinessUnitId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, "provider-webhook"));
        valid.Request.Headers["X-Nexora-Provider-Signature"] = ProviderSignature(secret, 42, true, request);
        Assert.IsType<OkObjectResult>(await valid.RecordDeliveryResult(42, true, request));
        Assert.Equal(1, recorder.DeliveryCalls);
    }

    [Fact]
    public async Task DunningRunPersistsCandidateDecisionAndExactNoticeArtifact()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        _ = await CreateFinalizedStatementAsync(service, "run-success-statement");
        var policy = await CreateActivePolicyAsync(service, "run-success-policy");
        var contact = await CreateCollectionsContactAsync(service, "run-success-contact");
        await AddProfileAsync(service, policy.Id, contact.Id, "en-US");

        var result = await service.RunDunningAsync(BusinessUnitId, "run-success",
            new CreateDunningRunRequest(policy.Id, DateTime.UtcNow), "run-operator");

        Assert.Equal("Completed", result.Status);
        var decision = Assert.Single(result.Decisions);
        Assert.Equal("NoticeCreated", decision.Outcome);
        Assert.Equal("POLICY_CANDIDATE_CREATED", decision.ReasonCode);
        Assert.NotNull(decision.DunningNoticeId);
        var notice = await db.DunningNotices.SingleAsync(x => x.Id == decision.DunningNoticeId);
        var item = await db.DunningCases.SingleAsync(x => x.Id == notice.DunningCaseId);
        Assert.Equal(64, notice.IdempotencyKey.Length);
        Assert.Equal(64, item.IdempotencyKey.Length);
        Assert.Equal(notice.ArtifactHash, (await service.GetNoticesAsync(
            BusinessUnitId, notice.DunningCaseId, null)).Single().ArtifactHash);
    }

    [Fact]
    public async Task MalformedDunningCandidateIsRecordedWithoutAbortingBatch()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        AddInvoice(db, 100m, DateTime.UtcNow.AddDays(-45), DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();
        _ = await CreateFinalizedStatementAsync(service, "run-failure-statement");
        var policy = await CreateActivePolicyAsync(service, "run-failure-policy");
        var contact = await CreateCollectionsContactAsync(service, "run-failure-contact");
        await AddProfileAsync(service, policy.Id, contact.Id, "en-US");
        var profile = await db.CustomerCollectionProfiles.SingleAsync();
        profile.Locale = "invalid locale";
        await db.SaveChangesAsync();

        var result = await service.RunDunningAsync(
            BusinessUnitId, "run-failure", new CreateDunningRunRequest(policy.Id, DateTime.UtcNow),
            "run-operator");

        db.ChangeTracker.Clear();
        var completed = await db.DunningRuns.SingleAsync();
        Assert.Equal("Completed", result.Status);
        Assert.Equal(1, completed.FailedCount);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseToken);
        Assert.Null(completed.LeaseUntil);
        var decision = await db.DunningRunDecisions.SingleAsync();
        Assert.Equal("Failed", decision.Outcome);
        Assert.Equal("INVALID_PROFILE_CONFIGURATION", decision.ReasonCode);
        Assert.Empty(await db.DunningNotices.ToListAsync());
    }

    [Fact]
    public async Task DunningRunRecoversExpiredLeaseButDoesNotStealActiveLease()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var policy = await CreateActivePolicyAsync(service, "lease-policy");
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var request = new CreateDunningRunRequest(policy.Id, cutoff);
        var requestHash = RequestHash(request);
        db.DunningRuns.AddRange(
            new DunningRun
            {
                BusinessUnitId = BusinessUnitId, DunningPolicyId = policy.Id, CutoffAt = cutoff,
                Status = "Running", IdempotencyKey = "expired-run", RequestHash = requestHash,
                LeaseOwner = "dead-worker", LeaseToken = Guid.NewGuid(), LeaseUntil = DateTime.UtcNow.AddMinutes(-1),
                Version = 2, CreatedBy = "run-maker", CreatedOn = DateTime.UtcNow.AddMinutes(-10)
            },
            new DunningRun
            {
                BusinessUnitId = BusinessUnitId, DunningPolicyId = policy.Id, CutoffAt = cutoff,
                Status = "Running", IdempotencyKey = "active-run", RequestHash = requestHash,
                LeaseOwner = "active-worker", LeaseToken = Guid.NewGuid(), LeaseUntil = DateTime.UtcNow.AddMinutes(4),
                Version = 2, CreatedBy = "run-maker", CreatedOn = DateTime.UtcNow.AddMinutes(-10)
            });
        await db.SaveChangesAsync();

        var recovered = await service.RunDunningAsync(BusinessUnitId, "expired-run", request, "recovery-worker");
        var active = await service.RunDunningAsync(BusinessUnitId, "active-run", request, "other-worker");

        Assert.Equal("Completed", recovered.Status);
        Assert.Equal("Running", active.Status);
        db.ChangeTracker.Clear();
        Assert.Equal("active-worker", (await db.DunningRuns.SingleAsync(
            x => x.IdempotencyKey == "active-run")).LeaseOwner);
    }

    [Fact]
    public async Task RecoveredDunningRunSkipsCommittedProfileCheckpoint()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenantAndCustomer(db);
        var service = CreateService(db);
        var policy = await CreateActivePolicyAsync(service, "checkpoint-policy");
        var contact = await CreateCollectionsContactAsync(service, "checkpoint-contact");
        await AddProfileAsync(service, policy.Id, contact.Id, "en-US");
        var profile = await db.CustomerCollectionProfiles.SingleAsync();
        var cutoff = DateTime.UtcNow.AddMinutes(-1);
        var request = new CreateDunningRunRequest(policy.Id, cutoff);
        var run = new DunningRun
        {
            BusinessUnitId = BusinessUnitId, DunningPolicyId = policy.Id, CutoffAt = cutoff,
            Status = "Running", CandidateCount = 1, IdempotencyKey = "checkpointed-run",
            RequestHash = RequestHash(request), LeaseOwner = "dead-worker", LeaseToken = Guid.NewGuid(),
            LeaseUntil = DateTime.UtcNow.AddMinutes(-1), Version = 2,
            CreatedBy = "run-maker", CreatedOn = DateTime.UtcNow.AddMinutes(-10)
        };
        db.DunningRuns.Add(run);
        await db.SaveChangesAsync();
        db.DunningRunDecisions.Add(new DunningRunDecision
        {
            BusinessUnitId = BusinessUnitId, DunningRunId = run.Id,
            CustomerCollectionProfileId = profile.Id, CustomerId = profile.CustomerId,
            CurrencyId = profile.CurrencyId, Outcome = "Skipped", ReasonCode = "NO_FINAL_STATEMENT",
            EvidenceHash = new string('a', 64), CreatedOn = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        var recovered = await service.RunDunningAsync(
            BusinessUnitId, "checkpointed-run", request, "recovery-worker");

        Assert.Equal("Completed", recovered.Status);
        Assert.Equal(1, recovered.CandidateCount);
        var decision = Assert.Single(recovered.Decisions);
        Assert.Equal(profile.Id, decision.CustomerCollectionProfileId);
        Assert.Single(await db.DunningRunDecisions.Where(x => x.DunningRunId == run.Id).ToListAsync());
    }

    private static ReceivablesOperationsController ControllerWithClaims(params Claim[] claims)
        => ControllerWithClaims(
            null!, new ConfigurationBuilder().AddInMemoryCollection().Build(), claims);

    private static ReceivablesOperationsController ControllerWithClaims(
        IReceivablesOperationsService service, IConfiguration configuration, params Claim[] claims)
    {
        var controller = new ReceivablesOperationsController(
            service, NullLogger<ReceivablesOperationsController>.Instance, configuration);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
        return controller;
    }

    private static ReceivableDocument AddInvoice(
        ErpRfqAutomationContext db, decimal amount, DateTime issuedOn, DateTime dueOn,
        string status = ReceivableDocumentStatuses.Issued, DateTime? voidedOn = null)
    {
        // SQLite maps decimal columns to text, so its relational decimal checks reject valid snapshots.
        db.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON;");
        var invoice = new ReceivableDocument
        {
            BusinessUnitId = BusinessUnitId,
            CustomerId = CustomerId,
            DocumentType = ReceivableDocumentTypes.Invoice,
            Status = status,
            DocumentNumber = $"INV-TEST-{Guid.NewGuid():N}",
            DocumentDate = issuedOn,
            DueDate = dueOn,
            IssuedOn = issuedOn,
            VoidedOn = voidedOn,
            SubTotal = amount,
            TotalAmount = amount,
            IdempotencyKey = $"invoice-{Guid.NewGuid():N}",
            RequestHash = TestHash(),
            CreatedBy = "invoice-maker",
            CreatedOn = issuedOn,
            IssuedBy = "invoice-checker"
        };
        db.ReceivableDocuments.Add(invoice);
        return invoice;
    }

    private static CustomerPayment AddPayment(
        ErpRfqAutomationContext db, decimal amount, DateTime paymentDate,
        string status = CustomerPaymentStatuses.Posted, DateTime? reversedOn = null)
    {
        var payment = new CustomerPayment
        {
            BusinessUnitId = BusinessUnitId,
            CustomerId = CustomerId,
            ReceiptNumber = $"RCT-TEST-{Guid.NewGuid():N}",
            Status = status,
            PaymentDate = paymentDate,
            Amount = amount,
            Method = "BankTransfer",
            IdempotencyKey = $"payment-{Guid.NewGuid():N}",
            RequestHash = TestHash(),
            ReversedOn = reversedOn,
            ReversalReason = reversedOn.HasValue ? "Provider reversal after historical cutoff" : null,
            CreatedBy = "collector-1",
            CreatedOn = paymentDate
        };
        db.CustomerPayments.Add(payment);
        return payment;
    }

    private static void AddPaymentAllocation(
        ErpRfqAutomationContext db, CustomerPayment payment, ReceivableDocument invoice, decimal amount)
        => db.PaymentAllocations.Add(new PaymentAllocation
        {
            BusinessUnitId = BusinessUnitId,
            CustomerPaymentId = payment.Id,
            ReceivableDocumentId = invoice.Id,
            Amount = amount,
            CreatedOn = payment.PaymentDate
        });

    private static ReceivableWriteOff AddWriteOff(
        ErpRfqAutomationContext db, ReceivableDocument invoice, decimal amount, DateTime accountingDate,
        string status, DateTime? reversedOn = null)
    {
        var writeOff = new ReceivableWriteOff
        {
            BusinessUnitId = BusinessUnitId,
            CustomerId = CustomerId,
            WriteOffNumber = $"WOF-TEST-{Guid.NewGuid():N}",
            Status = status,
            AccountingDate = accountingDate,
            TotalAmount = amount,
            ReasonCode = "SMALL_BALANCE",
            Reason = "Approved governed receivable write-off",
            EvidenceReference = "case:writeoff:test",
            PostingStatus = status == FinanceExceptionStatuses.Reversed ? "Reversed" : "Posted",
            IdempotencyKey = $"writeoff-{Guid.NewGuid():N}",
            RequestHash = TestHash(),
            CreatedBy = "writeoff-maker",
            CreatedOn = accountingDate.AddDays(-1),
            ApprovedBy = "writeoff-checker",
            ApprovedOn = accountingDate.AddMinutes(-1),
            ReversedBy = reversedOn.HasValue ? "writeoff-reverser" : null,
            ReversedOn = reversedOn,
            ReversalReason = reversedOn.HasValue ? "Write-off reversed after historical cutoff" : null,
            ReversalEvidenceReference = reversedOn.HasValue ? "case:writeoff:reversal" : null
        };
        db.ReceivableWriteOffs.Add(writeOff);
        db.SaveChanges();
        db.WriteOffAllocations.Add(new WriteOffAllocation
        {
            BusinessUnitId = BusinessUnitId,
            ReceivableWriteOffId = writeOff.Id,
            ReceivableDocumentId = invoice.Id,
            Amount = amount,
            BalanceBefore = invoice.TotalAmount,
            BalanceAfter = invoice.TotalAmount - amount
        });
        return writeOff;
    }

    private static CustomerRefund AddRefund(
        ErpRfqAutomationContext db, CustomerPayment sourcePayment, decimal amount, DateTime releasedOn,
        string status, DateTime? reversedOn = null)
    {
        var refund = new CustomerRefund
        {
            BusinessUnitId = BusinessUnitId,
            SourcePaymentId = sourcePayment.Id,
            CustomerId = CustomerId,
            RefundNumber = $"RFD-TEST-{Guid.NewGuid():N}",
            Status = status,
            RequestedExecutionDate = releasedOn,
            Amount = amount,
            Method = "BankTransfer",
            DestinationReference = "token:verified-refund-destination",
            DestinationVerified = true,
            ReasonCode = "CUSTOMER_REFUND",
            Reason = "Approved return of unapplied customer funds",
            EvidenceReference = "case:refund:test",
            PostingStatus = status == FinanceExceptionStatuses.Reversed ? "Reversed" : "Released",
            IdempotencyKey = $"refund-{Guid.NewGuid():N}",
            RequestHash = TestHash(),
            CreatedBy = "refund-maker",
            CreatedOn = releasedOn.AddDays(-1),
            ApprovedBy = "refund-checker",
            ApprovedOn = releasedOn.AddMinutes(-2),
            ReleasedBy = "refund-releaser",
            ReleasedOn = releasedOn,
            ReversedBy = reversedOn.HasValue ? "refund-reverser" : null,
            ReversedOn = reversedOn,
            ReversalReason = reversedOn.HasValue ? "Refund reversed after historical cutoff" : null,
            ReversalEvidenceReference = reversedOn.HasValue ? "case:refund:reversal" : null
        };
        db.CustomerRefunds.Add(refund);
        return refund;
    }

    private static async Task<CustomerStatementDto> CreateFinalizedStatementAsync(
        ReceivablesOperationsService service, string key)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-1);
        var draft = await service.CreateStatementAsync(BusinessUnitId, key,
            new CreateCustomerStatementRequest(
                CustomerId, null, cutoff.AddDays(-90), cutoff, null, "statement-v1"), "statement-maker");
        return await service.FinalizeStatementAsync(BusinessUnitId, draft.Id,
            new StatementActionRequest(draft.Version), "statement-checker");
    }

    private static async Task<DunningPolicyDto> CreateActivePolicyAsync(
        ReceivablesOperationsService service, string key, bool requiresApproval = false)
    {
        var currentHour = DateTime.UtcNow.Hour;
        var draft = await service.CreatePolicyAsync(BusinessUnitId, key, new CreateDunningPolicyRequest(
            "Focused regression policy", "US", "UTC", 0, 7, 1m,
            (currentHour + 1) % 24, (currentHour + 2) % 24, "policy-v1",
            [new DunningPolicyStepRequest(
                1, 1, 1m, 0, "Email", "notice-v1", requiresApproval, "Collector", 3)]), "policy-maker");
        var approved = await service.ApprovePolicyAsync(BusinessUnitId, draft.Id,
            new DunningPolicyActionRequest(draft.Version), "policy-checker");
        return await service.ActivatePolicyAsync(BusinessUnitId, draft.Id,
            new DunningPolicyActionRequest(approved.Version), "policy-operator");
    }

    private static Task<CustomerCollectionProfileDto> AddProfileAsync(
        ReceivablesOperationsService service, long policyId, long? contactId = null, string locale = "en")
        => service.UpsertCollectionProfileAsync(BusinessUnitId,
            new UpsertCustomerCollectionProfileRequest(
                CustomerId, null, policyId, contactId, locale, "UTC", "collector-1", true, null),
            "profile-owner");

    private static Task<FinanceCommunicationContactDto> CreateCollectionsContactAsync(
        ReceivablesOperationsService service, string key)
    {
        var request = new CreateFinanceCommunicationContactRequest(
            CustomerId, "Collections", "Email", $"token:{Guid.NewGuid():N}", "c***@example.com",
            DateTime.UtcNow.AddMinutes(-10), null, "provider-contact-verification", Guid.NewGuid(), string.Empty);
        return service.CreateContactAsync(BusinessUnitId, key, SignContactRequest(request), "contact-maker");
    }

    private static ReceivablesOperationsService CreateService(ErpRfqAutomationContext db)
        => new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CommercialFinance:ContactVerificationSecret"] = ContactVerificationSecret,
            ["CommercialFinance:DunningProviderWebhookSecret"] = DunningProviderSecret
        }).Build());

    private static CreateFinanceCommunicationContactRequest SignContactRequest(
        CreateFinanceCommunicationContactRequest request)
    {
        var effectiveFrom = NormalizeUtc(request.EffectiveFrom);
        var canonical = string.Join('\n', BusinessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.CustomerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Purpose.Trim(), request.Channel.Trim(), request.DestinationToken.Trim(),
            request.MaskedDestination.Trim(), UnixMilliseconds(effectiveFrom),
            request.EffectiveTo.HasValue ? UnixMilliseconds(NormalizeUtc(request.EffectiveTo.Value)) : string.Empty,
            request.VerificationEvidenceReference.Trim(), request.VerificationProviderEventId.ToString("D"));
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ContactVerificationSecret),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return request with { ProviderSignature = signature };
    }

    private static string RequestHash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })))).ToLowerInvariant();

    private static DunningCase AddCase(
        ErpRfqAutomationContext db, long statementId, long policyId, decimal exposure, DateTime oldestDueDate)
    {
        var item = new DunningCase
        {
            BusinessUnitId = BusinessUnitId,
            CustomerId = CustomerId,
            DunningPolicyId = policyId,
            CustomerStatementId = statementId,
            Status = DunningCaseStatuses.Open,
            ExposureAtOpen = exposure,
            CurrentExposure = exposure,
            OldestDueDate = oldestDueDate,
            NextActionOn = DateTime.UtcNow.AddMinutes(-1),
            IdempotencyKey = $"case-{Guid.NewGuid():N}",
            RequestHash = TestHash(),
            CreatedBy = "collector-1",
            CreatedOn = DateTime.UtcNow.AddMinutes(-2)
        };
        db.DunningCases.Add(item);
        return item;
    }

    private static decimal AgingTotal(CustomerStatementDto statement)
        => statement.AgingCurrent + statement.Aging1To30 + statement.Aging31To60 +
            statement.Aging61To90 + statement.AgingOver90;

    private static string TestHash()
        => Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();

    private static string ProviderSignature(
        string secret, long noticeId, bool delivered, DunningDeliveryResultRequest request)
    {
        var canonical = string.Join('\n', BusinessUnitId, noticeId, delivered.ToString().ToLowerInvariant(),
            request.ProviderEventId.ToString("D"), request.ProviderReference.Trim(),
            UnixMilliseconds(NormalizeUtc(request.ProviderOccurredOn)),
            request.FailureCode?.Trim(), request.SignedEvidenceReference.Trim());
        return Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static string UnixMilliseconds(DateTime value)
        => new DateTimeOffset(NormalizeUtc(value)).ToUnixTimeMilliseconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);

    public class RecordingReceivablesServiceProxy : DispatchProxy
    {
        public int DeliveryCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IReceivablesOperationsService.RecordDeliveryResultAsync))
            {
                DeliveryCalls++;
                return Task.FromResult<DunningNoticeDto>(null!);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private static long InvokeTenantId(ReceivablesOperationsController controller)
        => (long)typeof(ReceivablesOperationsController)
            .GetMethod("TenantId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, null)!;

    private static void SeedTenantAndCustomer(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Receivables Test Customer");
        db.SaveChanges();
    }

    private const long BusinessUnitId = 96_001;
    private const long CustomerId = 96_002;
}
