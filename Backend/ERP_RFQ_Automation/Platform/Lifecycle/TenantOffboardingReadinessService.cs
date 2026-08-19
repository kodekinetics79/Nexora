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

    /// <summary>
    /// Whether this tenant's books have to be closed and handed over before its records may be
    /// destroyed. False only for a tenant that never had a customer — see the rule in
    /// <see cref="TenantOffboardingReadinessService.AssessAsync"/>.
    ///
    /// <para>Exposed rather than re-derived by callers so the gate and the screen describing the
    /// gate cannot disagree. A console that said "commercial evidence not required" over a server
    /// that still required it would be worse than saying nothing.</para>
    /// </summary>
    Task<bool> CommercialEvidenceAppliesAsync(Tenant tenant, CancellationToken ct = default);
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

        // The commercial block below asks one question in five parts: were this customer's books
        // closed and handed over before their records were destroyed. On a tenant that never had a
        // customer there is no answer to give — and, worse, no way to give one. A never-invoiced
        // trial tenant cannot produce a Final billing statement, a finalized subscription invoice
        // or a reconciled acknowledgement receipt from an external accounting system, so every one
        // of those gates fails permanently and the tenant can never be deleted. That is not a
        // safeguard protecting anybody; it is an unsatisfiable condition, and before this change it
        // applied to every tenant on the platform, none of which had been through a billed cycle.
        //
        // So the block is skipped when it is provably VACUOUS — not waived. Two facts must hold,
        // and they guard different things:
        //
        //   * a non-PRODUCTION deployment profile, which is an Owner stating in writing, with a
        //     reason and an audit record, that this is not a customer. It is a decision somebody
        //     is accountable for, not an inference from an empty table.
        //   * no commercial footprint at all. This is the fact the label cannot fake: a tenant
        //     carrying a statement or an invoice has books to close, whatever it is labelled, and
        //     every gate below applies to it unchanged. Relabelling a tenant LOCAL_TEST can
        //     therefore never be used to walk away from reconciliation.
        //
        // Everything structural stays: archived-first, legal hold, personal-data erasure proof, the
        // export receipt, the retention window, two-person approval and the full audit trail.
        if (!await CommercialEvidenceAppliesAsync(tenant, ct))
        {
            // evidenceCompletedOn stays at the tenant's own last-modified instant, so the export
            // gate below still binds: an export has to be taken AFTER the tenant was last changed.
            // Archiving changes it, which is why the working order is archive, then export.
            return await AssessExportAsync(tenant, failures, evidenceCompletedOn, ct);
        }

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

        return await AssessExportAsync(tenant, failures, evidenceCompletedOn, ct);
    }

    /// <summary>
    /// The export gate, which binds on every tenant including one with no books to close.
    ///
    /// <para>Nothing is destroyed until this platform can prove the data came out first. On a
    /// tenant with a customer that is a handback; on one without, it is still the last cheap check
    /// that the export path works against this tenant's rows before the rows stop existing.</para>
    ///
    /// <para>The failure detail names the instant the export has to beat, because
    /// "after the financial closure evidence" is meaningless on a tenant that has none — and an
    /// operator reading it would go looking for billing evidence that was never required.</para>
    /// </summary>
    private async Task<TenantOffboardingReadinessResult> AssessExportAsync(
        Tenant tenant, List<TenantOffboardingReadinessFailure> failures,
        DateTime evidenceCompletedOn, CancellationToken ct)
    {
        var exportReceipt = await db.Set<TenantExportReceipt>().AsNoTracking()
            .Where(x => x.TenantId == tenant.Id)
            .OrderByDescending(x => x.CompletedOn).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
        if (exportReceipt is null || exportReceipt.CompletedOn < evidenceCompletedOn
                                  || exportReceipt.SizeBytes <= 0
                                  || !IsSha256(exportReceipt.ContentSha256))
            failures.Add(new(TenantOffboardingReadinessCodes.ExportReceiptMissing,
                "A valid customer export completed after "
                + $"{evidenceCompletedOn:yyyy-MM-dd HH:mm} UTC is required. Take the export after "
                + "archiving the tenant, because archiving moves that instant forward."));

        return new(failures.Count == 0, failures);
    }

    /// <inheritdoc />
    public async Task<bool> CommercialEvidenceAppliesAsync(
        Tenant tenant, CancellationToken ct = default)
        => tenant.DeploymentProfile == TenantDeploymentProfile.Production
           || await db.Set<BillingStatement>().AsNoTracking()
               .AnyAsync(x => x.TenantId == tenant.Id, ct)
           || await db.Set<SubscriptionInvoice>().AsNoTracking()
               .AnyAsync(x => x.TenantId == tenant.Id, ct);

    private static DateTime Max(DateTime value, DateTime? candidate) =>
        candidate is not null && candidate.Value > value ? candidate.Value : value;

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
