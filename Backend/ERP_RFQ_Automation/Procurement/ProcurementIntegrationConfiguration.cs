namespace ERP_RFQ_Automation.Procurement;

/// <summary>
/// Whether a business unit has an ERP / procurement connector configured, and the one place that
/// question is answered.
///
/// <para><b>This is the ONLY per-tenant integration configuration Nexora has.</b> There is no
/// integrations table, no connector registry and no per-tenant health record — a connector exists
/// exactly when this deployment's configuration names a source system and a shared secret for the
/// tenant's business unit. Two callers now depend on that fact: the callback endpoint, which
/// refuses to authenticate a connector that is not configured, and the activation gate, which must
/// not demand integration-health evidence from a tenant that has no integration. They have to read
/// the same predicate, because a tenant the gate calls "not integrated" while the endpoint
/// authenticates its callbacks is a tenant activated on a false statement.</para>
/// </summary>
public static class ProcurementIntegrationConfiguration
{
    /// <summary>Where a deployment declares one tenant's connector.</summary>
    public static string SectionFor(long businessUnitId) =>
        $"ProcurementIntegration:Tenants:{businessUnitId}";

    /// <summary>
    /// True when this business unit has a usable connector declared. The shared-secret length floor
    /// is part of "configured" rather than a separate validation: a connector whose secret is too
    /// short can never authenticate a callback, so treating it as present would give the tenant an
    /// integration that cannot work and an activation control that demands evidence for it.
    /// </summary>
    public static bool TryResolve(
        IConfiguration configuration, long businessUnitId, out string? sourceSystem, out string? sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionFor(businessUnitId));
        var source = section["SourceSystem"]?.Trim();
        var secret = section["SharedSecret"];
        var configured = !string.IsNullOrWhiteSpace(source) && source.Length <= 100
                         && !string.IsNullOrWhiteSpace(secret) && secret.Length >= 32;

        sourceSystem = configured ? source : null;
        sharedSecret = configured ? secret : null;
        return configured;
    }

    /// <summary>Convenience for callers that only need the yes/no.</summary>
    public static bool IsConfigured(IConfiguration configuration, long businessUnitId) =>
        TryResolve(configuration, businessUnitId, out _, out _);
}
