using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

public static class TenantOffboardingReadinessCodes
{
    public const string TenantStillServed = "TENANT_STILL_SERVED";
    public const string LegalHoldActive = "LEGAL_HOLD_ACTIVE";
    public const string FinalBillingMissing = "FINAL_BILLING_MISSING";
    public const string BillingReconciliationBlocked = "BILLING_RECONCILIATION_BLOCKED";
    public const string FinalInvoiceMissing = "FINAL_INVOICE_MISSING";
    public const string AccountsReceivableOpen = "ACCOUNTS_RECEIVABLE_OPEN";
    public const string AccountingAcknowledgementMissing = "ACCOUNTING_ACKNOWLEDGEMENT_MISSING";
    public const string ExportReceiptMissing = "EXPORT_RECEIPT_MISSING";
    public const string PersonalDataErasureMissing = "PERSONAL_DATA_ERASURE_MISSING";
}

public sealed record TenantOffboardingReadinessFailure(string Code, string Detail);

public sealed record TenantOffboardingReadinessResult(
    bool Ready, IReadOnlyList<TenantOffboardingReadinessFailure> Failures);

public enum TenantOffboardingReadinessPhase { Schedule, Purge }

public interface ITenantOffboardingReadinessService
{
    Task<TenantOffboardingReadinessResult> AssessAsync(
        Tenant tenant, TenantOffboardingReadinessPhase phase, CancellationToken ct = default);
}

/// <summary>
/// Fail-closed evidence gate shared by deletion scheduling and the final purge. It derives its
/// verdict only from persisted lifecycle, billing, AR, accounting-export, and customer-export
/// evidence; an operator cannot assert readiness in a request.
/// </summary>
public sealed class TenantOffboardingReadinessService(ErpRfqAutomationContext db)
    : ITenantOffboardingReadinessService
{
    private const decimal BalanceTolerance = 0.005m;

    public async Task<TenantOffboardingReadinessResult> AssessAsync(
        Tenant tenant, TenantOffboardingReadinessPhase phase, CancellationToken ct = default)
    {
        var failures = new List<TenantOffboardingReadinessFailure>();
        if (tenant.Status != TenantLifecycleGraph.DeletionRequiresStatus)
            failures.Add(new(TenantOffboardingReadinessCodes.TenantStillServed,
                $"Tenant is {tenant.Status}; only an archived tenant can enter offboarding."));

        if (await db.Set<TenantLegalHold>().AsNoTracking()
                .AnyAsync(x => x.TenantId == tenant.Id && x.ReleasedOn == null, ct))
            failures.Add(new(TenantOffboardingReadinessCodes.LegalHoldActive,
                "An active legal hold must be released through its governed workflow."));

        if (phase == TenantOffboardingReadinessPhase.Purge
            && !await db.Set<TenantOffboarding>().AsNoTracking()
                .AnyAsync(x => x.TenantId == tenant.Id && x.PersonalDataErasedOn != null, ct))
            failures.Add(new(TenantOffboardingReadinessCodes.PersonalDataErasureMissing,
                "Persisted personal-data erasure proof is required before destructive purge."));

        var statements = await db.Set<BillingStatement>().AsNoTracking()
            .Where(x => x.TenantId == tenant.Id)
            .OrderByDescending(x => x.PeriodEndUtc).ThenByDescending(x => x.Id)
            .ToListAsync(ct);
        var finalStatement = statements.FirstOrDefault();
        DateTime evidenceCompletedOn = tenant.ModifiedOn ?? tenant.CreatedOn;

        if (finalStatement is null)
        {
            failures.Add(new(TenantOffboardingReadinessCodes.FinalBillingMissing,
                "No final billing statement proves that terminal usage was reconciled."));
        }
        else
        {
            var validFinalEvidence = finalStatement.Status == BillingStatementStatus.Final
                                     && finalStatement.ReadinessStatus == BillingReadinessStatus.Ready
                                     && finalStatement.FinalizedAtUtc is not null
                                     && IsSha256(finalStatement.ReadinessManifestSha256)
                                     && finalStatement.PeriodEndUtc >= evidenceCompletedOn
                                     && statements.All(x => x.Status == BillingStatementStatus.Final);
            if (!validFinalEvidence)
                failures.Add(new(TenantOffboardingReadinessCodes.BillingReconciliationBlocked,
                    "The latest terminal billing period is not Final and Ready, does not cover the archived transition, or another Draft remains open."));
            evidenceCompletedOn = Max(evidenceCompletedOn, finalStatement.FinalizedAtUtc);

            var invoice = await db.Set<SubscriptionInvoice>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenant.Id
                                           && x.BillingStatementId == finalStatement.Id, ct);
            if (invoice is null || invoice.Status == SubscriptionInvoiceStatus.Draft
                                || invoice.FinalizedAtUtc is null)
            {
                failures.Add(new(TenantOffboardingReadinessCodes.FinalInvoiceMissing,
                    "The terminal Final statement has no finalized subscription invoice."));
            }
            else
            {
                evidenceCompletedOn = Max(evidenceCompletedOn, invoice.FinalizedAtUtc);
                var openAr = await db.Set<SubscriptionInvoice>().AsNoTracking()
                    .Where(x => x.TenantId == tenant.Id
                                && x.Status != SubscriptionInvoiceStatus.Void
                                && x.Status != SubscriptionInvoiceStatus.Corrected)
                    .AnyAsync(x => x.TotalAmount - x.CreditedAmount - x.PaidAmount > BalanceTolerance, ct);
                if (openAr)
                    failures.Add(new(TenantOffboardingReadinessCodes.AccountsReceivableOpen,
                        "One or more subscription invoices retain an uncollected AR balance."));

                var acknowledgement = await db.Set<AccountingOutboxMessage>().AsNoTracking()
                    .Where(x => x.TenantId == tenant.Id && x.SubscriptionInvoiceId == invoice.Id)
                    .OrderByDescending(x => x.AcknowledgedAtUtc).ThenByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(ct);
                if (acknowledgement is null
                    || acknowledgement.Status != AccountingOutboxStatus.Acknowledged
                    || acknowledgement.ReconciliationStatus != AccountingReconciliationStatus.Reconciled
                    || acknowledgement.AcknowledgedAtUtc is null
                    || !IsSha256(acknowledgement.ExternalReceiptSha256))
                    failures.Add(new(TenantOffboardingReadinessCodes.AccountingAcknowledgementMissing,
                        "The terminal invoice has no reconciled accounting acknowledgement receipt."));
                else
                    evidenceCompletedOn = Max(evidenceCompletedOn, acknowledgement.AcknowledgedAtUtc);
            }
        }

        var exportReceipt = await db.Set<TenantExportReceipt>().AsNoTracking()
            .Where(x => x.TenantId == tenant.Id)
            .OrderByDescending(x => x.CompletedOn).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (exportReceipt is null || exportReceipt.CompletedOn < evidenceCompletedOn
                                  || exportReceipt.SizeBytes <= 0
                                  || !IsSha256(exportReceipt.ContentSha256))
            failures.Add(new(TenantOffboardingReadinessCodes.ExportReceiptMissing,
                "A valid customer export completed after the financial closure evidence is required."));

        return new(failures.Count == 0, failures);
    }

    private static DateTime Max(DateTime value, DateTime? candidate) =>
        candidate is not null && candidate.Value > value ? candidate.Value : value;

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
