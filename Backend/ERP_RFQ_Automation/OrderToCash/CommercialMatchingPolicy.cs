namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// The per-tenant commercial policy row: the tolerances that decide whether a customer PO differs
/// from what we quoted (FR-COM-04), how much supplier input tax is a cost
/// (<see cref="SupplierInputTaxRecoverablePercent"/>), and the rate at which output tax is derived
/// on the sell side (<see cref="OutputTaxRatePercent"/>).
///
/// <para>The PO comparison used to be exact decimal equality — not a configurable tolerance, not
/// even a hardcoded one — so ordinary rounding noise read as a commercial discrepancy while the
/// requirement asks for a tolerance "such as plus or minus 2%". One row per business unit,
/// created on demand with the defaults below.</para>
///
/// <para>The BRD leaves the SCOPE of the tolerance open (decision E10: tenant, customer, or price
/// list?). This is the tenant-level answer, which is the level the requirement illustrates. It is
/// deliberately a table rather than a constant so a narrower scope can be added later without
/// moving where the value lives — and that is exactly why the two tax numbers live here too
/// rather than becoming silent constants somewhere in the pricing code.</para>
///
/// <para>Every field on this row is customer-settable through
/// <c>CommercialMatchingPolicyService</c> and the Commercial Policy settings screen, which is the
/// product owner's explicit direction: "keep it custom and let the customer set this... rather
/// than collecting figures for each of the country". A change re-bases every landed cost and every
/// derived tax computed after it, so a write requires a reason and stamps
/// <see cref="ModifiedBy"/>/<see cref="ModifiedOn"/>/<see cref="Version"/>.</para>
/// </summary>
public sealed class CommercialMatchingPolicy
{
    /// <summary>Full recovery — the KSA default for a VAT-registered tenant making taxable supplies.</summary>
    public const decimal FullyRecoverablePercent = 100m;

    /// <summary>The KSA standard rate. The default, not a hardcode: every tenant may set its own.</summary>
    public const decimal KsaStandardOutputTaxRatePercent = 15m;

    /// <summary>
    /// The policy every read falls back to when a tenant has no row yet. Reading it is what makes
    /// "create on demand" safe to skip on pure read paths: absence of a row means defaults, and
    /// the defaults are stated once, here, in the entity itself rather than at each call site.
    /// </summary>
    public static CommercialMatchingPolicy DefaultFor(long businessUnitId) =>
        new() { BusinessUnitId = businessUnitId };

    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>
    /// What SHARE of the tax a Supplier charges us on a purchase is recoverable from the tax
    /// authority, and therefore NOT a cost of the goods. 0–100.
    ///
    /// <para>Default 100, because the platform's home jurisdiction is Saudi Arabia and a
    /// VAT-registered business making taxable supplies reclaims its input VAT in full: the 15% the
    /// supplier adds to an invoice is a receivable from ZATCA, not money the goods consumed.
    /// Carrying it in landed cost overstated cost, and because a customer price is derived as
    /// landed / (1 - margin), the overstatement was then MARKED UP by the margin before output VAT
    /// was added again on the customer quote — the same tax counted twice, once with profit on
    /// top.</para>
    ///
    /// <para>Set 0 for a business unit that cannot reclaim input tax at all — one that is not
    /// VAT-registered, or that trades exclusively in exempt supplies — for which the supplier's tax
    /// genuinely is a cost of acquisition and belongs in landed cost in full.</para>
    ///
    /// <para><b>Why a percentage and not a boolean</b> (decision R18). A VAT-registered trader that
    /// makes PARTLY exempt supplies recovers input VAT pro-rata, and the irrecoverable share is a
    /// cost of the asset under IAS 2.11. A boolean forces that tenant to choose between overstating
    /// cost (0/false: the whole VAT lands in cost) and understating it (100/true: none of it does),
    /// and there is no third option to select. A tenant with a 70% recovery ratio sets 70 and the
    /// remaining 30% of the supplier's tax lands in cost, which is what the standard requires.</para>
    ///
    /// <para>This is still ONE named number per business unit and not a tax-code engine. Decision R8
    /// fixes the cost model at "landed price, margin, tax" and rules out per-line jurisdiction or
    /// recoverability inference: "the more complex the harder for them". Freight, duty and other
    /// captured charges are unaffected — they are never recoverable and always land in cost.</para>
    /// </summary>
    public decimal SupplierInputTaxRecoverablePercent { get; set; } = FullyRecoverablePercent;

    /// <summary>
    /// The rate at which OUTPUT tax is derived on a standard-rated customer quote line. Default 15,
    /// the KSA standard rate.
    ///
    /// <para>Nullable, and null means NOT CONFIGURED rather than zero. A tenant whose rate is
    /// unknown must not silently quote tax-free: <c>OutputTaxFormula.Derive</c> returns null, the
    /// line's tax is never derived, and the send gate refuses the quote until someone states the
    /// rate. Zero is a different, positive assertion — "our output tax rate really is 0%" — and is
    /// spelled 0.</para>
    ///
    /// <para><b>Why this exists</b> (decision R17). Nothing on the sell side derived output tax:
    /// the line's tax was operator-typed, defaulted to null, and validation rejected only negative
    /// amounts. That error was silently cancelling the input-VAT error R15 removed, because both
    /// legs are 15% on nearly the same base. Under KSA law a price carrying no separately stated
    /// VAT is DEEMED VAT-inclusive, so the seller owes 15/115 ≈ 13.04% out of it — which on a 20%
    /// target margin destroys most of the gross profit.</para>
    ///
    /// <para>One rate per business unit, applied to the category the user chose on the line. It is
    /// not a tax-code engine and infers nothing.</para>
    /// </summary>
    public decimal? OutputTaxRatePercent { get; set; } = KsaStandardOutputTaxRatePercent;

    /// <summary>Percentage difference tolerated between the quoted and ordered unit price.</summary>
    public decimal PriceTolerancePercent { get; set; } = 2.0m;

    /// <summary>
    /// Absolute difference tolerated regardless of percentage.
    ///
    /// <para>A percentage alone misbehaves at small values: a line quoted at 0.10 and ordered at
    /// 0.11 is a 10% swing and almost certainly rounding, while the same percentage on a 50,000
    /// line is a real commercial difference. The two together mean a discrepancy is reported when
    /// it is both proportionally and absolutely material.</para>
    /// </summary>
    public decimal PriceToleranceMinimumAmount { get; set; } = 0m;

    /// <summary>
    /// Percentage difference tolerated between the quoted and ordered quantity. Defaults to zero:
    /// a quantity is a count the buyer stated, so any difference is a real award decision — a
    /// partial award — and not noise to be absorbed.
    /// </summary>
    public decimal QuantityTolerancePercent { get; set; } = 0m;

    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}
