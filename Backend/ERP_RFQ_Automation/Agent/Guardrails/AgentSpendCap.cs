using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Guardrails;

/// <summary>
/// What a cap comparison concluded. Three states, not two — because "I could not compare these"
/// is a genuinely different answer from "this is over the cap", and collapsing it into either
/// bucket is how the defect below got in.
/// </summary>
public enum AgentCapOutcome
{
    /// <summary>Converted with approved evidence and found at or below the cap.</summary>
    WithinCap,

    /// <summary>Converted with approved evidence and found above the cap.</summary>
    ExceedsCap,

    /// <summary>
    /// No comparison was possible. The amount or the cap has no currency, or no approved rate
    /// joins the pair. Callers MUST treat this exactly as they treat <see cref="ExceedsCap"/>:
    /// route to a human.
    /// </summary>
    Unconvertible
}

/// <summary>
/// The outcome of one cap comparison plus the sentence to show the human it is being handed to.
/// <see cref="MayAutoExecute"/> is deliberately the only boolean: there is no way to spell
/// "not over the cap" that also accepts <see cref="AgentCapOutcome.Unconvertible"/>.
/// </summary>
public sealed record AgentCapVerdict(AgentCapOutcome Outcome, string Reason)
{
    public bool MayAutoExecute => Outcome == AgentCapOutcome.WithinCap;
}

/// <summary>
/// The single authority for "is this amount within the tenant's agent spend cap?".
///
/// <para><b>The defect this exists to close.</b> The caps on <see cref="AgentPolicy"/> were bare
/// decimals with no currency, and every comparison site tested them directly against an amount
/// denominated in a supplier quote's own currency — <c>SupplierQuotedItem.CurrencyId</c> — with
/// no conversion. A cap of 10,000 stopped a 10,000 SAR award and a 10,000 USD award alike. At
/// roughly 3.75 SAR to the dollar the same configured ceiling authorised nearly four times as
/// much unattended spend depending only on which currency a supplier quoted in. This governs
/// what the platform may commit WITHOUT a human, so it is a control defect, not a display bug.
///
/// <para><b>Where rates come from.</b> Nothing is invented here. This type is a thin caller of
/// <see cref="IFxConversionService"/> (Fx/FxConversionService.cs), the pre-existing FX authority:
/// effective-dated, approval-gated <see cref="FxRate"/> rows, resolved identity -&gt; direct -&gt;
/// inverse -&gt; triangulated via the business unit's base currency. Only rows with
/// <see cref="FxRateStatuses.Approved"/> are visible to it. There is no fallback to 1, no
/// fallback to <c>Currency.ExchangeRate</c>, no external FX call, and no hardcoded rate.
///
/// <para><b>Fail closed, three ways.</b> Conversion is impossible when (a) the policy has no
/// <see cref="AgentPolicy.CurrencyId"/>, so the cap denotes no definite amount; (b) the amount
/// has no currency, so it denotes no definite amount either; or (c) no approved rate joins the
/// pair on the as-of date. In all three the answer is <see cref="AgentCapOutcome.Unconvertible"/>
/// with a message naming exactly what is missing. There is no path from here to a raw numeric
/// comparison and no path that assumes the currencies match. An amount that cannot be converted
/// is precisely the amount a human should be looking at.
/// </summary>
public sealed class AgentSpendCap
{
    private readonly ErpRfqAutomationContext _db;
    private readonly IFxConversionService _fx;

    /// <summary>
    /// Constructed over the context directly, following the house precedent for this collaborator
    /// (RecommendAwardTool.cs:85, QuoteRepository.cs:445, DashboardRepository.cs:43,
    /// BelowFloorGuard.cs:193). FxConversionService is stateless over the context, so this needs
    /// no DI wiring and leaves existing direct construction of the guardrail and tools working.
    /// </summary>
    public AgentSpendCap(ErpRfqAutomationContext db)
        : this(db, new FxConversionService(db))
    {
    }

    /// <summary>Seam for substituting the FX authority in tests.</summary>
    public AgentSpendCap(ErpRfqAutomationContext db, IFxConversionService fx)
    {
        _db = db;
        _fx = fx;
    }

    /// <summary>
    /// Converts <paramref name="amount"/> into the policy's currency using approved,
    /// effective-dated evidence, then compares. The cap is inclusive: an amount exactly equal to
    /// the cap is <see cref="AgentCapOutcome.WithinCap"/>, preserving the existing "at or below"
    /// contract.
    /// </summary>
    /// <param name="amountCurrencyId">
    /// The currency the amount is denominated in. Null is NOT "same as the cap" — it is
    /// unconvertible, and it is the single most important null in this file.
    /// </param>
    /// <param name="label">"award" or "order"; used only to word the message.</param>
    public async Task<AgentCapVerdict> EvaluateAsync(
        long businessUnitId,
        decimal amount,
        long? amountCurrencyId,
        AgentPolicy policy,
        decimal cap,
        string label,
        DateTime asOf,
        CancellationToken ct = default)
    {
        // (a) The cap has no denomination. Nothing about it can be compared to anything.
        if (policy.CurrencyId is null)
            return Unconvertible(
                $"This tenant's agent policy has no cap currency configured, so the {label} auto-execute cap " +
                $"of {cap:0.##} does not denote a definite amount and cannot be compared with a " +
                $"{label} value. Set the agent policy currency before the copilot can auto-execute; " +
                "until then every amount-bearing action goes to a human.");

        // (b) The amount has no denomination. Same problem, other operand.
        if (amountCurrencyId is null)
        {
            var capCodeOnly = await CurrencyCodeAsync(businessUnitId, policy.CurrencyId.Value, ct);
            return Unconvertible(
                $"This {label} value ({amount:0.##}) carries no currency, so it cannot be compared with the " +
                $"auto-execute cap of {cap:0.##} {capCodeOnly}. Record the currency on the underlying " +
                "commercial record; this action requires human approval.");
        }

        // (c) Ask the FX authority. Identity (same currency both sides) resolves to a rate of
        // exactly 1 inside ResolveRateAsync — it is not special-cased here, so there is one code
        // path and a removed conversion cannot hide behind a same-currency test.
        var resolution = await _fx.ResolveRateAsync(businessUnitId, amountCurrencyId.Value, policy.CurrencyId.Value, asOf, ct);
        if (!resolution.Found)
        {
            var amountCode = await CurrencyCodeAsync(businessUnitId, amountCurrencyId.Value, ct);
            var capCode = await CurrencyCodeAsync(businessUnitId, policy.CurrencyId.Value, ct);
            return Unconvertible(
                $"This {label} is quoted in {amountCode} and the auto-execute cap is set in {capCode}, but " +
                $"{resolution.Reason} The copilot will not guess a rate, so this {label} requires human approval.");
        }

        var converted = FxConversionService.RoundMoney(amount * resolution.Rate);
        var amountCurrencyCode = await CurrencyCodeAsync(businessUnitId, amountCurrencyId.Value, ct);
        var capCurrencyCode = await CurrencyCodeAsync(businessUnitId, policy.CurrencyId.Value, ct);

        // Show the original AND the converted figure. A human reading "12,000 USD (44,070 SAR)
        // exceeds the 40,000 SAR cap" can audit the conversion; "12,000 exceeds 40,000" cannot
        // even be checked, and read as a bare number it looks like a false positive.
        var rendered = amountCurrencyId.Value == policy.CurrencyId.Value
            ? $"{converted:0.##} {capCurrencyCode}"
            : $"{amount:0.##} {amountCurrencyCode} ({converted:0.##} {capCurrencyCode} at {resolution.Rate:0.##########}, {resolution.ResolutionPath})";

        return converted > cap
            ? new AgentCapVerdict(AgentCapOutcome.ExceedsCap,
                $"{Capitalise(label)} value {rendered} exceeds the auto-execute cap of {cap:0.##} {capCurrencyCode}; requires approval.")
            : new AgentCapVerdict(AgentCapOutcome.WithinCap,
                $"{Capitalise(label)} value {rendered} is within the auto-execute cap of {cap:0.##} {capCurrencyCode}.");
    }

    private static AgentCapVerdict Unconvertible(string reason) =>
        new(AgentCapOutcome.Unconvertible, reason);

    /// <summary>
    /// The tenant-scoped code, or the raw id when the currency row is missing. Explicitly filters
    /// BusinessUnitId rather than leaning on a query filter, matching FxConversionService — this
    /// is display text on a control decision and must not leak another tenant's currency name.
    /// </summary>
    private async Task<string> CurrencyCodeAsync(long businessUnitId, long currencyId, CancellationToken ct)
    {
        var code = await _db.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.Id == currencyId)
            .Select(c => c.Code)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(code) ? $"currency #{currencyId}" : code;
    }

    private static string Capitalise(string label) =>
        string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];
}
