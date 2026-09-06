using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// What this deployment pays for AI, as one rate card.
///
/// <para><b>Why this is not a tenant setting.</b> It used to be six fields on every tenant's AI
/// policy — input and output cost per million tokens, currency, pricing version, local compute per
/// hour, OCR per page — retyped by an operator for every customer. None of them is a fact about a
/// customer. The price of a million <c>deepseek-v4-pro</c> tokens at <c>https://ollama.com</c> is
/// the same for Intelliflow as for everybody else on this deployment, and the cost of running OCR
/// is a property of the hardware, not of who is being invoiced.</para>
///
/// <para>Asking a salesperson for it produced exactly what asking always produces: a tenant
/// carrying <c>ExternalCostCurrency = "1"</c>, which passes the API's "not empty" check, violates
/// the database's <c>^[A-Z]{3}$</c> constraint, and returns to the operator as
/// <c>An unexpected error occurred</c>. A value nobody should have been asked for cannot be
/// mistyped.</para>
///
/// <para><b>Why configuration.</b> It sits beside the two settings that already decide the same
/// thing — <c>Ollama:BaseUrl</c> and <c>Ollama:Model</c> name the endpoint whose prices these are.
/// A rate is a property of the destination this installation was deployed against, so it is
/// declared where the destination is declared, and it changes when the deployment changes rather
/// than per customer.</para>
/// </summary>
public sealed record AiRateCard(
    decimal? ExternalInputCostPerMillionTokens,
    decimal? ExternalOutputCostPerMillionTokens,
    string? Currency,
    /// <summary>
    /// Stamped onto every priced ledger row, so a cost can always be traced back to the rate that
    /// produced it after the rate has moved on.
    /// </summary>
    string? PricingVersion,
    decimal? LocalComputeCostPerHour,
    decimal? OcrCostPerPage)
{
    public const string Section = "Ai:RateCard";

    /// <summary>Nothing is priced without all four. Half a rate card prices nothing.</summary>
    public bool CanPriceExternal =>
        ExternalInputCostPerMillionTokens is >= 0
        && ExternalOutputCostPerMillionTokens is >= 0
        && IsCurrencyCode(Currency)
        && !string.IsNullOrWhiteSpace(PricingVersion);

    /// <summary>
    /// The ISO-4217 shape the database enforces. Checked HERE as well, so a malformed rate card is
    /// a deployment that prices nothing and says so on the ledger row, rather than a constraint
    /// violation surfacing as an unexplained failure at settle time.
    /// </summary>
    public static bool IsCurrencyCode(string? value) =>
        value is { Length: 3 } code && code.All(char.IsAsciiLetterUpper);

    public static AiRateCard Empty { get; } = new(null, null, null, null, null, null);

    public static AiRateCard Read(IConfiguration? configuration)
    {
        if (configuration is null) return Empty;
        var section = configuration.GetSection(Section);
        return new AiRateCard(
            section.GetValue<decimal?>("ExternalInputCostPerMillionTokens"),
            section.GetValue<decimal?>("ExternalOutputCostPerMillionTokens"),
            section.GetValue<string?>("Currency")?.Trim().ToUpperInvariant(),
            section.GetValue<string?>("PricingVersion")?.Trim(),
            section.GetValue<decimal?>("LocalComputeCostPerHour"),
            section.GetValue<decimal?>("OcrCostPerPage"));
    }
}

/// <summary>The deployment's rate card, resolved once at startup.</summary>
public interface IAiRateCardProvider
{
    AiRateCard Current { get; }
}

public sealed class AiRateCardProvider(IConfiguration configuration) : IAiRateCardProvider
{
    public AiRateCard Current { get; } = AiRateCard.Read(configuration);
}
