namespace ERP_RFQ_Automation.Platform.Activation;

/// <summary>
/// The console screen that owns the fix for a blocking activation control.
///
/// <para>These are console addresses, not routes: the tenant ones are tabs on the tenant detail
/// page and the platform ones are top-level pages. They are strings rather than an enum so the
/// console can switch on them without a generated client, and so adding a screen is a one-line
/// change on both sides.</para>
/// </summary>
public static class ActivationRemediationSurfaces
{
    public const string TenantActivation = "tenant.activation";
    public const string TenantProfileAccess = "tenant.profile-access";
    public const string TenantCommercial = "tenant.commercial";
    public const string TenantDataStorage = "tenant.data-storage";
    public const string TenantModules = "tenant.modules";
    public const string PlatformPlans = "platform.plans";
}

/// <summary>
/// What the console does when the operator presses Resolve.
///
/// <para>Every one of these names an action the console ALREADY performs from some other screen,
/// against the endpoint that already owns it. Nothing here is a new privileged verb: the remedy
/// for a blocking control is the ordinary edit, taken by whoever is ordinarily allowed to take
/// it, audited under the ordinary action name. A "resolve activation control" endpoint would be
/// an escape hatch wearing a helpful label.</para>
/// </summary>
public static class ActivationRemediationActions
{
    /// <summary>PUT /api/platform/tenants/{id}/profile — legal identity, country, contact, tax number.</summary>
    public const string TenantProfileIdentity = "tenant.profile-identity";

    /// <summary>PUT /api/platform/tenants/{id}/plan.</summary>
    public const string TenantPlanAssignment = "tenant.plan-assignment";

    /// <summary>PUT /api/platform/billing/tenants/{id}/account-contact.</summary>
    public const string TenantAccountContact = "tenant.account-contact";

    /// <summary>PUT /api/platform/billing/tenants/{id}/rate-card.</summary>
    public const string TenantRateCardPin = "tenant.rate-card-pin";

    /// <summary>PUT /api/platform/billing/tenants/{id}/commercial-terms.</summary>
    public const string TenantCommercialTerms = "tenant.commercial-terms";

    /// <summary>
    /// POST /api/platform/tenants/{id}/data-assets, then
    /// POST /api/platform/tenants/{id}/data-assets/{assetId}/verify. One action because which of
    /// the two is next is a fact about the registry, not a decision for the operator to get wrong.
    /// </summary>
    public const string TenantDataAssetBoundary = "tenant.data-asset-boundary";

    /// <summary>POST /api/platform/tenants/{id}/activation/controls/{code}/evidence.</summary>
    public const string TenantActivationEvidence = "tenant.activation-evidence";

    /// <summary>POST|PUT /api/platform/plans — the plan catalogue, not this tenant.</summary>
    public const string PlatformPlanEntitlements = "platform.plan-entitlements";

    /// <summary>PUT /api/platform/tenants/{id}/modules — this customer's own module grants.</summary>
    public const string TenantModuleGrants = "tenant.module-grants";
}

/// <summary>
/// Which authority the remedy needs, named after the server policy that will actually decide.
///
/// <para>The console gates the Resolve button on this instead of guessing. Guessing has exactly
/// two failure modes and the console had both: offer the button to somebody the server will 403,
/// or hide it from somebody who could have fixed it in ten seconds. The second is worse, because
/// nothing tells them it happened.</para>
/// </summary>
public static class ActivationRemediationAuthorities
{
    /// <summary><c>Platform.Owner</c>.</summary>
    public const string Owner = "Owner";

    /// <summary><c>Platform.Billing</c> — Owner or BillingAdmin.</summary>
    public const string Billing = "Billing";

    /// <summary><c>Platform.TenantAdmin</c> — Owner or SupportAdmin.</summary>
    public const string TenantAdmin = "TenantAdmin";

    /// <summary>
    /// <c>Platform.Owner</c> AND <c>Platform.Mfa</c>. A separate value rather than Owner, because
    /// an Owner on a password-only session is refused by the second policy and the console must
    /// be able to say so before the request rather than after it.
    /// </summary>
    public const string OwnerMfa = "OwnerMfa";
}

/// <summary>
/// The map from a blocking activation control to the screen that owns its fix.
///
/// <para><b>The defect.</b> Every remedy endpoint already existed, every one of them already had a
/// console screen and a client method, and the activation decision named its blockers as bare
/// control codes — <c>commercial.rate-card</c>, <c>billing.account-recipient</c>. Nothing anywhere
/// said which of eleven tabs owned which code. An operator provisioning one tenant made eleven form
/// submissions across twelve surfaces, and a real tenant sat unactivatable for three days because
/// the person holding it could not work out where <c>data.residency-isolation</c> was satisfied
/// from. This catalogue is that missing sentence, written down once.</para>
///
/// <para><b>Static, and deliberately not configurable.</b> There is no table, no seed and no
/// admin screen for it. A remediation route that a deployment can edit is a remediation route that
/// can be pointed at the wrong screen, and the failure mode — an operator sent confidently to a tab
/// that cannot fix the control — is worse than the bare code they had before.</para>
///
/// <para><b>It changes nothing about the verdict.</b> Nothing in here is read by
/// <see cref="TenantActivationPolicyService"/> when it decides whether a control passes. It is
/// navigation. A control with a remediation is not closer to satisfied than one without, and the
/// four unresolvable controls below are not weaker for having no button.</para>
/// </summary>
public static class ActivationControlRemediationCatalog
{
    /// <summary>
    /// One control's entry: a remedy, or a RECORDED decision that there is none.
    ///
    /// <para>Exactly one of the two is populated, enforced in the static constructor. The pairing
    /// is the point: "this control has no resolver" has to be a sentence somebody wrote and can be
    /// held to, not the silence left behind by a control nobody got round to mapping. A missing
    /// entry is a bug, and the exhaustiveness test fails on it.</para>
    /// </summary>
    private sealed record Entry(ActivationControlRemediation? Remediation, string? NoRemedyReason);

    private static Entry Remedy(string surface, string action, string label, string authority, string hint) =>
        new(new ActivationControlRemediation(surface, action, label, authority, hint), null);

    private static Entry NoRemedy(string reason) => new(null, reason);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
    {
        // --- controls an operator can actually resolve -------------------------------------
        ["identity.legal-customer"] = Remedy(
            ActivationRemediationSurfaces.TenantProfileAccess,
            ActivationRemediationActions.TenantProfileIdentity,
            "Record the legal identity",
            ActivationRemediationAuthorities.TenantAdmin,
            "Legal name, registration number, ISO alpha-2 country and the customer's contact email are "
            + "written together by the audited profile edit. None of them is inferred from the trading "
            + "name: a workspace called \"Acme\" is not evidence that a company called Acme signed anything."),

        ["commercial.plan"] = Remedy(
            ActivationRemediationSurfaces.TenantCommercial,
            ActivationRemediationActions.TenantPlanAssignment,
            "Assign a plan",
            ActivationRemediationAuthorities.Billing,
            "The plan is what the tenant is charged and what its quotas are read from, so assignment is a "
            + "commercial decision and carries the Billing policy. Only an active plan can be assigned."),

        ["billing.account-recipient"] = Remedy(
            ActivationRemediationSurfaces.TenantCommercial,
            ActivationRemediationActions.TenantAccountContact,
            "Set the invoicing details",
            ActivationRemediationAuthorities.Billing,
            "Invoice recipient, invoice address and positive payment terms. Invoicing refuses to issue "
            + "without a recipient, so a tenant missing this one is activated into a state where it can "
            + "never be billed and can never be offboarded."),

        ["billing.currency-tax"] = Remedy(
            ActivationRemediationSurfaces.TenantProfileAccess,
            ActivationRemediationActions.TenantProfileIdentity,
            "Record the tax identity",
            ActivationRemediationAuthorities.TenantAdmin,
            "The tax number is part of the audited profile edit. The billing currency is the platform's "
            + "(USD, carried on the pinned rate card) and is independent of the tenant's own base currency, "
            + "which is the base its quotes and ledger are kept in and is fixed at provisioning. A tenant "
            + "quoting in SAR bills in USD without being re-provisioned. The other way past this control is "
            + "an explicit Internal or Partner billing treatment, which is a commercial decision and lives "
            + "on Commercial."),

        ["commercial.rate-card"] = Remedy(
            ActivationRemediationSurfaces.TenantCommercial,
            ActivationRemediationActions.TenantRateCardPin,
            "Pin a rate card",
            ActivationRemediationAuthorities.Billing,
            "Pins the price list this tenant's statements are computed against. Only a card that is "
            + "active, in the platform billing currency (USD), effective today and carrying at least one priced "
            + "meter satisfies the control — creating or repricing a card is a different act and stays "
            + "on the Billing page."),

        ["commercial.approved-terms"] = Remedy(
            ActivationRemediationSurfaces.TenantCommercial,
            ActivationRemediationActions.TenantCommercialTerms,
            "Approve the commercial dates",
            ActivationRemediationAuthorities.Billing,
            "This control reads two screens' worth of fields: the contract start and end dates are part "
            + "of the invoicing details, and the billing start date is part of the commercial terms. Both "
            + "are on Commercial and both carry the Billing policy."),

        // Points at the tenant, not the plan catalogue, because 20260818013530 moved module access
        // onto the customer. The old remedy sent the operator to a screen whose every edit moved
        // every tenant on that plan — which is exactly the blast radius this control usually does
        // NOT want. The plan half of the control (unbounded seat/document/extraction limits) is
        // still a plan fix and the hint says so, rather than pretending one screen owns both.
        ["entitlements.typed-hard-limits"] = Remedy(
            ActivationRemediationSurfaces.TenantModules,
            ActivationRemediationActions.TenantModuleGrants,
            "Set this customer's modules",
            ActivationRemediationAuthorities.Billing,
            "Every module and capability has to be explicitly on or off for this customer before it "
            + "can be activated — an undecided one is not the same as a denied one. If the control "
            + "still blocks after that, the remaining half is the PLAN's seat, document or extraction "
            + "limit being unbounded, and fixing that on the plan moves every tenant on it."),

        ["security.privileged-mfa-policy"] = Remedy(
            ActivationRemediationSurfaces.TenantActivation,
            ActivationRemediationActions.TenantActivationEvidence,
            "Record the MFA attestation",
            ActivationRemediationAuthorities.OwnerMfa,
            "The tenant identity plane persists no MFA assurance, so this control is satisfied only by an "
            + "Owner-approved attestation pointing at a real, externally retained evidence artifact. The "
            + "form records the attestation; it does not check the tenant's identity provider and must "
            + "never be filled in as if it had."),

        ["data.residency-isolation"] = Remedy(
            ActivationRemediationSurfaces.TenantDataStorage,
            ActivationRemediationActions.TenantDataAssetBoundary,
            "Register or verify the data boundary",
            ActivationRemediationAuthorities.Owner,
            "The primary PostgreSQL tenant scope has to be registered in the contracted region with a "
            + "versioned backup policy, and then verified against an external probe. Which of the two "
            + "steps is next is a fact about the registry, so the dialog reads it rather than asking."),

        ["integrations.mandatory"] = Remedy(
            ActivationRemediationSurfaces.TenantActivation,
            ActivationRemediationActions.TenantActivationEvidence,
            "Record the integration evidence",
            ActivationRemediationAuthorities.OwnerMfa,
            "Either current health evidence for the customer's mandatory integrations, or an explicit "
            + "deferral with an effective window. A deferral is a dated statement that the integration is "
            + "absent, not a claim that it works."),

        // --- controls with deliberately NO resolver -----------------------------------------
        //
        // Each of these is a recorded decision, not an omission. A button that cannot honestly
        // change the underlying fact is worse than no button: the first time an operator presses
        // one and the control stays red, every OTHER Resolve button on this screen becomes
        // something they have learned to distrust.
        ["admin.first-activated"] = NoRemedy(
            "There is deliberately no resolver. This control asserts that a person who is NOT the "
            + "operator took possession of the workspace by redeeming their invitation and signing in, so "
            + "anything on this console that could satisfy it would be the operator forging that "
            + "assertion — which is precisely the fact the control exists to establish. The console shows "
            + "the invitation's status and offers to reissue or withdraw the link on Profile & access; "
            + "the customer redeeming it is the only thing that clears the control."),

        ["audit.health"] = NoRemedy(
            "There is deliberately no resolver. The control passes once at least one privileged action "
            + "against this tenant has been persisted successfully to the platform audit log, which is a "
            + "consequence of the ordinary work of provisioning and configuring the tenant. A button that "
            + "wrote an audit row in order to satisfy the control that reads audit rows would be a loop "
            + "that proves nothing about whether auditing works."),

        ["provisioning.completed-verified"] = NoRemedy(
            "There is deliberately no resolver. The control reads whether provisioning reached a terminal "
            + "successful state, which is owned by the provisioning execution and not by anything an "
            + "operator can assert. A stuck or failed execution is diagnosed and retried from the "
            + "Provisioning tab — that retry is the real remedy, and it works by making the fact true "
            + "rather than by declaring it."),

        ["data.lifecycle-operational"] = NoRemedy(
            "There is deliberately no resolver. The control fails only when this tenant has a scheduled "
            + "deletion, a started purge or a completed erasure on its record. Reversing any of those is a "
            + "governed lifecycle decision with its own reason, its own approver and its own audit action "
            + "on the Lifecycle tab; attaching it to an activation screen would let a destruction order be "
            + "cancelled as a side effect of trying to turn a tenant on."),
    };

    static ActivationControlRemediationCatalog()
    {
        // Fail at startup rather than shipping an entry that is neither a remedy nor a recorded
        // decision — a half-filled entry would read as "mapped" to the exhaustiveness test while
        // giving the operator nothing.
        foreach (var (code, entry) in Entries)
        {
            if (entry.Remediation is null == string.IsNullOrWhiteSpace(entry.NoRemedyReason))
                throw new InvalidOperationException(
                    $"Activation control '{code}' must have either a remediation or a recorded reason "
                    + "for having none, and never both or neither.");
        }
    }

    /// <summary>Every control code this catalogue has an opinion about. Ordering is not meaningful.</summary>
    public static IReadOnlyCollection<string> Codes => Entries.Keys;

    /// <summary>
    /// Whether this control is catalogued at all. False is the drift signal: a control that
    /// <see cref="TenantActivationPolicyService"/> evaluates and this catalogue has never heard of
    /// is control #15, added without anybody deciding where an operator goes to fix it.
    /// </summary>
    public static bool Covers(string? controlCode) =>
        !string.IsNullOrWhiteSpace(controlCode) && Entries.ContainsKey(controlCode);

    /// <summary>The screen that owns the fix, or null when the control has no resolver by design.</summary>
    public static ActivationControlRemediation? ForControl(string? controlCode) =>
        string.IsNullOrWhiteSpace(controlCode) ? null
            : Entries.TryGetValue(controlCode, out var entry) ? entry.Remediation : null;

    /// <summary>
    /// Why this control has no resolver, or null when it has one. Non-null exactly when
    /// <see cref="ForControl"/> is null and <see cref="Covers"/> is true.
    /// </summary>
    public static string? NoRemedyReason(string? controlCode) =>
        string.IsNullOrWhiteSpace(controlCode) ? null
            : Entries.TryGetValue(controlCode, out var entry) ? entry.NoRemedyReason : null;
}
