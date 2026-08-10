using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// Refuses recoverable input-tax treatment on a supplier line when the supplier holds no tax
/// registration number.
///
/// <para><b>What is actually being decided here.</b> <c>LandedCostFormula.CostBearingTax</c> keeps
/// recoverable supplier input tax OUT of landed cost (decisions R15/R18). That is not a costing
/// convention — it is an accounting position: the tax is booked as a receivable from ZATCA rather
/// than as a cost of the goods. Booking a receivable is booking a reclaim, and Article 49 of the
/// KSA VAT Implementing Regulations allows a reclaim only against a valid supplier tax invoice,
/// which must carry the SUPPLIER's VAT registration number. If we cannot name the counterparty to
/// the authority, the deduction is disallowed and the tax lands back in cost after the fact —
/// after it has already been excluded from the margin, marked up through
/// <c>landed / (1 - margin)</c>, and quoted to a customer.</para>
///
/// <para><b>Fail closed, not fail quiet.</b> The alternative — silently reverting the line to 0%
/// recoverable — would change the landed cost, and therefore the customer price, with nobody
/// told. A rep would see a different number and have no way to learn why. So this throws, and the
/// message names the supplier, the missing field and the two ways out (capture the TRN, or set the
/// tenant's recoverable percentage to 0 if it genuinely cannot reclaim).</para>
///
/// <para><b>What this does NOT establish.</b> A registration number on the supplier master is a
/// claim the supplier made to us; it is not a tax invoice. There is no supplier-invoice aggregate
/// in this codebase — the code says so itself at
/// <c>CommercialDocuments/CommercialDocumentClassificationService.FindInvalidMatchReferencesAsync</c>,
/// which rejects any client-supplied <c>SupplierInvoiceId</c> because no authoritative record
/// exists to point at. Until one does, this guard makes the reclaim position ATTRIBUTABLE (we can
/// say whose tax we are reclaiming) but not yet SUBSTANTIATED (we cannot produce the document that
/// proves it). Building that aggregate is a product-owner scope decision, not something to start
/// from inside a costing guard.</para>
/// </summary>
public static class SupplierInputTaxRecoverabilityGuard
{
    /// <summary>
    /// Throws when this tenant would treat any part of <paramref name="supplierTaxAmount"/> as
    /// recoverable and the supplier has no tax registration number on file.
    ///
    /// <para>No tax charged, or a tenant policy of 0% recovery, means no reclaim is being booked
    /// and there is nothing to substantiate — both pass. A tenant that cannot reclaim input tax at
    /// all is not made to chase registration numbers it has no use for.</para>
    /// </summary>
    public static async Task EnsureSupplierInputTaxIsClaimableAsync(this ErpRfqAutomationContext context,
        long businessUnitId, long supplierId, decimal supplierTaxAmount,
        CancellationToken cancellationToken = default)
        => await context.EnsureSupplierInputTaxIsClaimableAsync(businessUnitId, supplierId, supplierTaxAmount,
            await context.ResolveSupplierInputTaxRecoverablePercentAsync(businessUnitId, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Same rule, for the callers that have already read the tenant's recoverable percentage for
    /// the round they are costing. They pass it in rather than making this re-read it, so every
    /// line of one capture is checked against the same answer the arithmetic used — and so the
    /// guard's own decision is testable without a policy row.
    /// </summary>
    public static async Task EnsureSupplierInputTaxIsClaimableAsync(this ErpRfqAutomationContext context,
        long businessUnitId, long supplierId, decimal supplierTaxAmount,
        decimal supplierInputTaxRecoverablePercent, CancellationToken cancellationToken = default)
    {
        if (supplierTaxAmount <= 0m) return;

        var recoverablePercent = supplierInputTaxRecoverablePercent;
        if (recoverablePercent <= 0m) return;

        var supplier = await context.Suppliers.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Buid == businessUnitId && x.Id == supplierId)
            .Select(x => new { x.Name, x.TaxRegistrationNumber })
            .SingleOrDefaultAsync(cancellationToken);
        if (supplier is null)
            throw new SupplierInputTaxEvidenceMissingException(
                $"Supplier {supplierId} was not found in this business unit, so the input tax it " +
                "charged cannot be treated as recoverable.");

        if (!string.IsNullOrWhiteSpace(supplier.TaxRegistrationNumber)) return;

        throw new SupplierInputTaxEvidenceMissingException(
            $"Supplier '{supplier.Name}' has no tax registration number, so the " +
            $"{decimal.Round(recoverablePercent, 2)}% of its charged tax that this business unit " +
            "treats as recoverable cannot be claimed: an input-VAT deduction has to name the " +
            "supplier's VAT registration to the authority. Add the supplier's tax registration " +
            "number on the supplier record and retry — or, if this business unit genuinely cannot " +
            "reclaim input tax, set its commercial policy's supplier input tax recoverable " +
            "percentage to 0 so the tax is costed instead.");
    }
}

/// <summary>
/// Raised when a recoverable input-tax treatment cannot be evidenced. Derives from
/// <see cref="InvalidOperationException"/> so it lands in the same controller handling as
/// <c>ProcurementValidationException</c> / <c>SupplierQuoteValidationException</c> — a refused
/// command with a reason, not a server fault.
/// </summary>
public sealed class SupplierInputTaxEvidenceMissingException(string message)
    : InvalidOperationException(message);
