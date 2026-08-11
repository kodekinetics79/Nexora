using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Platform.Activation;

/// <summary>
/// How an activation control ended up where it is.
///
/// <para>The old contract had one bit — <c>Satisfied</c> — and therefore exactly two stories to
/// tell: it passed, or it is your problem. That bit could not distinguish "this customer has not
/// supplied their billing address" from "this workspace runs on a laptop and the customer's SAP
/// instance does not exist", and the second one is not a defect anybody can fix by trying
/// harder. Conflating them produced the only two bad outcomes available: a local test environment
/// that could never be brought up, or an operator quietly relaxing the production gate to make it
/// come up — which is the failure this vocabulary exists to make unnecessary.</para>
/// </summary>
public static class ActivationControlDispositions
{
    /// <summary>The control passes. The only disposition that is ever evidence of anything.</summary>
    public const string Satisfied = "SATISFIED";

    /// <summary>
    /// The control fails and stops activation here and now. Every unsatisfied control on a
    /// PRODUCTION tenant is this, without exception.
    /// </summary>
    public const string Blocking = "BLOCKING";

    /// <summary>
    /// The control fails, this deployment profile is entitled to defer it, and the missing
    /// prerequisite is something THIS side could stand up and has not (a private ClamAV cluster,
    /// production backup/PITR evidence). Still a production blocker; still fatal to certification.
    /// </summary>
    public const string Deferred = "DEFERRED";

    /// <summary>
    /// The control fails, this deployment profile is entitled to defer it, and the missing
    /// prerequisite belongs to somebody else entirely — the customer's storage estate, their ERP,
    /// their identity provider, a tax authority. No amount of work on this machine satisfies it.
    /// Still a production blocker; still fatal to certification.
    /// </summary>
    public const string ExternallyBlocked = "EXTERNALLY_BLOCKED";
}

/// <summary>
/// Which deployment profiles may defer a production prerequisite, and on what evidence.
/// </summary>
public static class DeploymentProfilePolicy
{
    /// <summary>
    /// Whether this tenant may record a catalogued prerequisite as DEFERRED / EXTERNALLY_BLOCKED
    /// instead of BLOCKING.
    ///
    /// <para><b>PRODUCTION is never entitled, under any condition.</b> There is deliberately no
    /// override, no configuration key and no evidence artifact that turns this true for a
    /// production tenant: an escape hatch that exists is an escape hatch that gets used at 2am.</para>
    ///
    /// <para><b>DEMO is entitled only once approved.</b> An unapproved DEMO tenant — one whose
    /// profile was set by something other than the audited Owner endpoint, e.g. a hand-written
    /// UPDATE or a restored row — defers nothing and fails closed exactly as PRODUCTION. That is
    /// what stops the profile column itself from becoming the escape hatch.</para>
    /// </summary>
    public static bool PermitsDeferral(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.DeploymentProfile switch
        {
            TenantDeploymentProfile.LocalTest => true,
            TenantDeploymentProfile.Demo => IsApproved(tenant),
            _ => false
        };
    }

    /// <summary>
    /// A non-production profile carries an approval when the audited endpoint recorded who
    /// approved it, when, and why. All three, because any one of them alone is satisfiable by
    /// accident.
    /// </summary>
    public static bool IsApproved(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return !string.IsNullOrWhiteSpace(tenant.DeploymentProfileApprovedBy)
               && tenant.DeploymentProfileApprovedOn is not null
               && !string.IsNullOrWhiteSpace(tenant.DeploymentProfileReason);
    }

    /// <summary>One sentence naming the profile's effect, for the console and the audit metadata.</summary>
    public static string Describe(Tenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.DeploymentProfile switch
        {
            TenantDeploymentProfile.Production =>
                "PRODUCTION: every activation control is a hard gate and nothing is deferrable.",
            TenantDeploymentProfile.LocalTest =>
                "LOCAL_TEST: catalogued external prerequisites may be deferred for activation. They remain "
                + "production blockers and this tenant cannot be certified production-ready.",
            TenantDeploymentProfile.Demo when IsApproved(tenant) =>
                $"DEMO (approved by {tenant.DeploymentProfileApprovedBy} on "
                + $"{tenant.DeploymentProfileApprovedOn:yyyy-MM-dd}): catalogued external prerequisites may be "
                + "deferred for activation. They remain production blockers and this tenant cannot be "
                + "certified production-ready.",
            _ =>
                "DEMO without a recorded Owner approval: nothing is deferred and this tenant fails closed "
                + "exactly as PRODUCTION until the profile is set through the audited endpoint."
        };
    }
}

/// <summary>
/// The closed list of production prerequisites a non-production profile may defer, and the
/// activation control each one governs.
///
/// <para><b>A closed list, not a rule.</b> Deferral is not "anything an operator marks as
/// external" — it is these six, named, each tied to the control it explains. Anything outside this
/// catalogue blocks in every profile, including LOCAL_TEST, which is why a broken plan assignment
/// or an unactivated founding administrator still stops a local test workspace: those are defects,
/// not absent third parties.</para>
/// </summary>
public static class DeploymentPrerequisiteCatalog
{
    /// <param name="Key">Stable machine identifier. Appears in the decision payload and in audit metadata.</param>
    /// <param name="Title">Operator-facing name.</param>
    /// <param name="ControlCode">
    /// The activation control this prerequisite explains, or null when the prerequisite is real
    /// but is not (and must not become) an activation gate — see <see cref="PrivateClamAv"/>.
    /// </param>
    /// <param name="DeferredDisposition">
    /// <see cref="ActivationControlDispositions.Deferred"/> when this side could stand the
    /// prerequisite up, <see cref="ActivationControlDispositions.ExternallyBlocked"/> when it
    /// belongs to a third party.
    /// </param>
    /// <param name="ProductionRequirement">What a production tenant must actually have. Shown verbatim.</param>
    public sealed record Definition(
        string Key,
        string Title,
        string? ControlCode,
        string DeferredDisposition,
        string ProductionRequirement);

    public const string ClientSuppliedStorage = "storage.client-supplied";
    public const string ProductionBackupPitr = "backup.production-pitr";
    public const string PrivateClamAv = "antivirus.private-clamav";
    public const string ErpAccountingIntegration = "integration.erp-accounting";
    public const string EnterpriseSsoScim = "identity.enterprise-sso-scim";
    public const string ProductionTaxCertification = "tax.production-certification";

    private static readonly Definition[] Definitions =
    [
        new(ClientSuppliedStorage, "Client-supplied object storage",
            "data.residency-isolation", ActivationControlDispositions.ExternallyBlocked,
            "The customer's own storage account is registered as a verified tenant data asset in the "
            + "contracted region, with a versioned backup policy attached to it."),

        new(ProductionBackupPitr, "Production backup and point-in-time recovery evidence",
            "data.residency-isolation", ActivationControlDispositions.Deferred,
            "A versioned backup policy and a dated restore-drill evidence artifact exist for this "
            + "tenant's database scope."),

        // Deliberately governs NO activation control. Malware scanning is a platform-wide posture,
        // not a per-tenant one: making it an activation gate would mean a misconfigured scanner
        // blocked every tenant's activation rather than every tenant's UPLOAD, which is where it is
        // already enforced fail-closed. It appears here because production-readiness certification
        // is exactly the place a deployment should have to answer for it.
        new(PrivateClamAv, "Private ClamAV malware scanning",
            null, ActivationControlDispositions.Deferred,
            "DocumentInspection:Scanner:Provider is ClamAV and DocumentInspection:ClamAV points at a "
            + "reachable, non-loopback clamd this deployment owns."),

        new(ErpAccountingIntegration, "ERP / accounting integration",
            "integrations.mandatory", ActivationControlDispositions.ExternallyBlocked,
            "The customer's ERP or accounting system is connected and its mandatory-integration "
            + "health evidence is current."),

        new(EnterpriseSsoScim, "Enterprise SSO / SCIM and privileged MFA assurance",
            "security.privileged-mfa-policy", ActivationControlDispositions.ExternallyBlocked,
            "The customer's identity provider is federated and an Owner-approved attestation covers "
            + "privileged MFA for this tenant."),

        new(ProductionTaxCertification, "Production tax identity and certification",
            "billing.currency-tax", ActivationControlDispositions.ExternallyBlocked,
            "The tenant's tax registration is recorded and the production tax-authority certification "
            + "that depends on it has been issued.")
    ];

    public static IReadOnlyList<Definition> All => Definitions;

    /// <summary>
    /// The prerequisite that explains a control, or null when the control is not deferrable in any
    /// profile.
    ///
    /// <para>Where two prerequisites govern one control — <c>data.residency-isolation</c> is
    /// governed by both the customer's storage and this side's backup evidence — the
    /// EXTERNALLY_BLOCKED one wins. It is the stronger statement about who can act, and reporting
    /// the weaker one would tell an operator to go and produce evidence for an asset that does not
    /// exist yet.</para>
    /// </summary>
    public static Definition? ForControl(string? controlCode)
    {
        if (string.IsNullOrWhiteSpace(controlCode)) return null;
        Definition? best = null;
        foreach (var definition in Definitions)
        {
            if (!string.Equals(definition.ControlCode, controlCode, StringComparison.Ordinal)) continue;
            if (best is null || definition.DeferredDisposition == ActivationControlDispositions.ExternallyBlocked)
                best = definition;
        }

        return best;
    }
}

/// <summary>
/// The deployment-wide (not per-tenant) facts production-readiness certification has to answer for.
///
/// <para>Separate from the tenant policy service because it reads process configuration rather
/// than the database, and because a test that wants to assert on the tenant policy should not have
/// to stand up a configuration root to do it.</para>
/// </summary>
public interface IPlatformDeploymentPosture
{
    /// <summary>
    /// Whether uploads in this deployment are scanned by a private ClamAV at a configured
    /// endpoint, and the sentence that explains the answer either way.
    /// </summary>
    (bool Satisfied, string Detail) PrivateMalwareScanning();
}

/// <summary>
/// Reads the same selection <c>Program.cs</c> logs at startup, so the certification surface and
/// the running scanner can never disagree.
/// </summary>
public sealed class PlatformDeploymentPosture(IConfiguration configuration, IHostEnvironment environment)
    : IPlatformDeploymentPosture
{
    public (bool Satisfied, string Detail) PrivateMalwareScanning()
    {
        var selection = MalwareScannerFactory.Select(configuration, environment.IsDevelopment());

        if (selection.Provider != MalwareScannerProvider.ClamAV)
            return (false,
                $"Uploads are inspected by the {selection.ProviderName} provider, which is not an "
                + "anti-virus engine and detects no real malware.");

        if (selection.ConfigurationError is { Length: > 0 } error)
            return (false, $"The ClamAV connection settings are not usable: {error}");

        if (selection.EndpointLooksUnconfigured)
            return (false,
                $"ClamAV is selected but its endpoint ({selection.Endpoint}) is still loopback outside "
                + "Development, so no clamd is being reached.");

        return (true, $"ClamAV is scanning uploads at {selection.Endpoint}.");
    }
}
