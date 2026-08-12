using ERP_RFQ_Automation.Procurement;

namespace ERP_RFQ_Automation.Platform.Activation;

/// <summary>One integration a tenant is configured to depend on.</summary>
/// <param name="Key">Stable machine identifier, e.g. <c>procurement.erp-connector</c>.</param>
/// <param name="Detail">One sentence naming what is configured and where it was read from.</param>
public sealed record MandatoryIntegration(string Key, string Detail);

/// <summary>
/// What integrations THIS tenant is configured to depend on — the question
/// <c>integrations.mandatory</c> should have been asking all along.
/// </summary>
public interface ITenantMandatoryIntegrationInventory
{
    /// <summary>
    /// Every mandatory integration configured for a business unit. An EMPTY list is a positive
    /// finding — "this tenant has no mandatory integration" — and is not the same as being unable
    /// to look, which is what a null <paramref name="businessUnitId"/> means and is reported by
    /// returning null.
    /// </summary>
    IReadOnlyList<MandatoryIntegration>? ForBusinessUnit(long? businessUnitId);
}

/// <summary>
/// Reads the only per-tenant integration configuration this product has.
///
/// <para><b>The defect.</b> <c>integrations.mandatory</c> demanded current health evidence, or an
/// explicit dated deferral, from EVERY tenant unconditionally — including the overwhelming
/// majority that have no integration configured at all. There was nothing to be healthy, so an
/// operator's only route to activation was to record a "deferral" of an integration that did not
/// exist: a dated, Owner-attested statement about nothing, filed for no reader. A gate that can
/// only be passed by writing something untrue teaches operators that the evidence forms are
/// paperwork, and the next one they fill in carelessly is one that mattered.</para>
///
/// <para><b>"Nothing is configured" is a fact, not a waiver.</b> This class establishes it by
/// reading the same configuration the callback endpoint authenticates against — see
/// <see cref="ProcurementIntegrationConfiguration"/> — so the gate and the endpoint can never
/// disagree about whether a tenant has an ERP connector. The moment one IS configured the control
/// goes back to demanding current evidence or an explicit deferral, because from that moment there
/// genuinely is something whose health somebody has to answer for.</para>
///
/// <para><b>What is deliberately NOT here.</b> There is no persisted per-tenant integration
/// registry in Nexora — no table, no entity, no migration — so this reads configuration and says
/// so. When a real registry lands, this interface is where it plugs in, and the activation gate
/// does not change.</para>
/// </summary>
public sealed class ConfiguredMandatoryIntegrationInventory(IConfiguration configuration)
    : ITenantMandatoryIntegrationInventory
{
    public const string ProcurementErpConnector = "procurement.erp-connector";

    public IReadOnlyList<MandatoryIntegration>? ForBusinessUnit(long? businessUnitId)
    {
        // No business unit is not "no integrations" — it is a tenant whose workspace does not exist
        // yet, and there is nothing to look up. Returning null keeps the control on its old,
        // evidence-demanding behaviour rather than passing it on an absence nobody established.
        if (businessUnitId is not long unit || unit <= 0)
            return null;

        var configured = new List<MandatoryIntegration>(1);
        if (ProcurementIntegrationConfiguration.TryResolve(configuration, unit, out var source, out _))
            configured.Add(new MandatoryIntegration(
                ProcurementErpConnector,
                $"An ERP / procurement connector for '{source}' is configured at "
                + $"{ProcurementIntegrationConfiguration.SectionFor(unit)}."));

        return configured;
    }
}
