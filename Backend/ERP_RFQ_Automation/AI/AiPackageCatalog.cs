namespace ERP_RFQ_Automation.AI;

/// <summary>
/// What a plan SELLS as its AI package, and exactly what each package means.
///
/// <para><b>Why a named package rather than eleven fields on a plan.</b> The settings that decide
/// whether a tenant's documents are read are commercial terms — they are agreed in a sales
/// conversation and then typed into a console by somebody who was not in it. Left as raw fields
/// they drift from the deal in both directions: a pilot account handed unlimited cloud
/// extraction, or a paying account provisioned with everything shut. A package is the unit the
/// deal is actually done in, and it is the unit the plan should carry.</para>
///
/// <para><b>Every package prints its own meaning.</b> <see cref="WhatItTurnsOn"/> is rendered
/// beside the package wherever it is chosen — on the plan editor and in the tenant's guided
/// setup. A tier whose meaning is not printed next to it is an opaque number with a friendly
/// name, and the operator ends up guessing what they sold.</para>
///
/// <para><b>What a package deliberately CANNOT carry: consent.</b> A plan may say that a customer
/// bought cloud extraction. It may not say that the customer agreed to send whole document text
/// off their own infrastructure — that decision belongs to a named human, per tenant, with a
/// written justification, and a pre-ticked box is not a decision anybody made. So
/// <see cref="Cloud"/> provisions a tenant ready to be approved, and stops there:
/// <c>EgressPolicy</c> stays at the strict default until somebody answers the question.</para>
/// </summary>
public sealed record AiPackageDefinition(
    string Key,
    string Name,
    /// <summary>The sentence a salesperson can say to a customer without qualifying it.</summary>
    string SoldAs,
    /// <summary>The posture a tenant on this package is provisioned with.</summary>
    string Posture,
    /// <summary>Purposes the package includes. Empty for a package that reads nothing.</summary>
    string[] Purposes,
    /// <summary>Printed beside the package everywhere it is chosen. Not marketing — the mapping.</summary>
    string WhatItTurnsOn);

public static class AiPackages
{
    public const string Off = "Off";
    public const string Private = "Private";
    public const string Cloud = "Cloud";

    private static readonly string[] ExtractionPurposes = [AiPurposes.RfqExtraction, AiPurposes.BoqDraft];

    public static readonly IReadOnlyList<AiPackageDefinition> All =
    [
        new(Off, "Manual",
            "No AI reads anything. People key the RFQ in.",
            AiPostures.Off, [],
            "AI processing is off for the tenant. Extraction, BOQ drafting and the assistant are "
            + "all unavailable, and no document is read by a model — locally or anywhere else."),

        new(Private, "Private extraction",
            "The model runs inside the customer's own deployment. Nothing leaves their infrastructure.",
            AiPostures.PrivateOnly, ExtractionPurposes,
            "AI processing on, external processing OFF, whole-document egress shut. Reads RFQ "
            + "documents and drafts bills of quantity on a local inference endpoint. Needs this "
            + "installation to actually have one — on a deployment whose every destination is "
            + "off-host, this package refuses every document."),

        new(Cloud, "Cloud extraction",
            "An approved external provider, named and revocable, with the customer's written consent.",
            AiPostures.ApprovedCloud, ExtractionPurposes,
            "AI processing on and external processing ALLOWED for the endpoint this installation "
            + "resolves. It does NOT grant whole-document egress: the tenant is provisioned ready "
            + "to be approved, and a named Owner still has to answer what may be sent, with a "
            + "justification, before a single document leaves the customer's infrastructure.")
    ];

    public static bool IsKnown(string? key) => Find(key) is not null;

    public static AiPackageDefinition? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The package a plan sells, or <see cref="Off"/> when it sells none. Never guesses: a plan
    /// with an unrecognised value reads as Off rather than as whatever looks closest, because the
    /// failure of "closest" here is a customer's documents going somewhere nobody agreed to.
    /// </summary>
    public static AiPackageDefinition Resolve(string? key) => Find(key) ?? All[0];
}

/// <summary>
/// Turning a plan's package into a tenant's starting policy, and telling afterwards whether that
/// tenant still matches what it was sold.
/// </summary>
public static class AiPackageProvisioning
{
    /// <summary>
    /// Applies a plan's package to a freshly provisioned policy row. Called from BOTH provisioning
    /// paths and the local seeder, because a starting posture that depends on which code path
    /// created the tenant is not a starting posture.
    ///
    /// <para><b>It never turns external processing on.</b> A Cloud package provisions a tenant
    /// READY to be approved — the purposes it was sold, the allowance it was sold — and stops at
    /// the consent question. Egress stays at the strict default until a named human answers it
    /// with a justification. That is not caution for its own sake: a pre-ticked consent is not a
    /// decision anybody made, and the DB's own CK_AiProcessingPolicies_TrustControls refuses
    /// external processing without a named provider and model in any case.</para>
    /// </summary>
    public static void Apply(
        AiProcessingPolicy policy, string? packageKey, long? allowance, bool allowanceUnlimited,
        string actor, DateTime now)
    {
        // NO PLAN is not a plan that sells nothing. A tenant provisioned without one keeps the
        // secure default it was created with — reading Resolve(null) as the Off package would
        // silently disable AI for every plan-less tenant, which is neither what anybody chose nor
        // what the codebase does elsewhere: an unplanned tenant gets a modest allowance
        // (UnplannedTenantAllowance), never a refusal.
        if (packageKey is null) return;

        var package = AiPackages.Resolve(packageKey);

        policy.IsEnabled = package.Posture != AiPostures.Off;
        if (package.Purposes.Length > 0)
            policy.AllowedPurposes = string.Join(',', package.Purposes);
        // Unlimited only when the plan says so out loud. An absent allowance on an AI package is
        // refused at the plan endpoint, so reaching here with neither is a plan written before
        // that rule existed — and the safe reading of "nobody decided" is not "no ceiling".
        policy.MonthlyHardTokenLimit = allowanceUnlimited ? null : allowance;

        policy.UpdatedOn = now;
        policy.UpdatedBy = actor;
    }

    /// <summary>
    /// The tenant's posture as its policy row currently stands, in the vocabulary the plan and the
    /// operator both use.
    /// </summary>
    public static string PostureOf(AiProcessingPolicy policy) =>
        !policy.IsEnabled ? AiPostures.Off
        : policy.ExternalProcessingAllowed ? AiPostures.ApprovedCloud
        : AiPostures.PrivateOnly;

    private static int Permissiveness(string posture) => posture switch
    {
        AiPostures.ApprovedCloud => 2,
        AiPostures.PrivateOnly => 1,
        _ => 0
    };

    /// <summary>
    /// What this tenant has that its plan did not sell — in plain sentences, ready to render.
    /// Empty means the tenant is within its plan.
    ///
    /// <para><b>Only ever MORE permissive counts.</b> A tenant configured below its plan is not a
    /// deviation needing a signature: it is a customer who has not finished setting up, or one who
    /// deliberately tightened something. Tightening a control never needs justifying — the same
    /// asymmetry the deployment profile already applies — and demanding a reason for it trains
    /// operators to type "n/a" into the field that is supposed to mean something.</para>
    /// </summary>
    public static IReadOnlyList<string> Deviations(
        AiProcessingPolicy policy, string? packageKey, long? allowance, bool allowanceUnlimited)
    {
        var package = AiPackages.Resolve(packageKey);
        var deviations = new List<string>();

        var posture = PostureOf(policy);
        if (Permissiveness(posture) > Permissiveness(package.Posture))
            deviations.Add(
                $"Reads documents with {Describe(posture)}, beyond the {package.Name} package this "
                + "tenant's plan sells.");

        var sold = package.Purposes;
        var extra = policy.AllowedPurposes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !sold.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (extra.Length > 0)
            deviations.Add($"Uses AI for {string.Join(", ", extra)}, which the plan's package does not include.");

        // Uncapped where the plan sold a ceiling is the expensive direction, and the one that
        // reaches a finance conversation months later with nobody able to say who agreed to it.
        //
        // Not asked at all of a tenant whose AI is OFF. Such a tenant has no ceiling because it
        // spends nothing, and reporting that as "beyond plan" would demand a signed exception for
        // the safest state a tenant can be in.
        if (posture != AiPostures.Off && !allowanceUnlimited && allowance is { } sold_allowance)
        {
            if (policy.MonthlyHardTokenLimit is null)
                deviations.Add(
                    $"Has no monthly AI ceiling at all, against the {sold_allowance:N0} tokens the plan sells.");
            else if (policy.MonthlyHardTokenLimit > sold_allowance)
                deviations.Add(
                    $"Has a monthly AI ceiling of {policy.MonthlyHardTokenLimit:N0} tokens, above the "
                    + $"{sold_allowance:N0} the plan sells.");
        }

        return deviations;
    }

    private static string Describe(string posture) => posture switch
    {
        AiPostures.ApprovedCloud => "an approved cloud provider",
        AiPostures.PrivateOnly => "a private model",
        _ => "nothing"
    };
}
