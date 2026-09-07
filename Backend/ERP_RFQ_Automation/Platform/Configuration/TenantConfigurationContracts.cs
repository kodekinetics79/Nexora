using ERP_RFQ_Automation.Platform.Activation;

namespace ERP_RFQ_Automation.Platform.Configuration;

/// <summary>
/// One read of everything the tenant console needs to render a customer, so that a screen no
/// longer has to be a tab strip mirroring the controllers behind it.
///
/// <para>WHY THIS EXISTS. The tenant detail screen fired eleven separate reads across eight
/// controllers, one per tab, and each tab then wrote back a full object through its own endpoint
/// with no shared version. The cost was not only twelve tabs: it was that no screen could ever
/// state a customer's overall position, because no single answer to "where does this customer
/// stand" existed on the wire. This is that answer.</para>
///
/// <para>WHAT IT DELIBERATELY DOES NOT DO. It is a READ. It does not become one giant
/// write. The slices below carry genuinely different authorities — commercial terms are
/// Owner-or-BillingAdmin, profile and users are TenantAdmin, AI and data-residency are
/// Owner-only — and collapsing them into a single PUT would hand the lowest-privileged operator
/// who can edit any slice the ability to edit all of them. Screens may merge; authorities must
/// not. <see cref="TenantConfigurationSlice.Editable"/> tells the console what THIS caller may
/// change, so one screen can render every slice while each stays behind its own endpoint and
/// keeps its own audit verb.</para>
/// </summary>
/// <param name="Version">
/// The tenant row's concurrency token, surfaced so the console can echo it back as
/// <c>If-Match</c>. A screen that merges several tabs makes stale-overwrite MORE likely, not
/// less, because one commit now carries edits a person may have started ten minutes ago.
/// </param>
public sealed record TenantConfigurationView(
    long TenantId,
    long Version,
    TenantConfigurationState State,
    IReadOnlyList<TenantConfigurationSlice> Slices,
    TenantActivationDecision? Activation,
    IReadOnlyList<TenantConfigurationBlocker> Blockers,
    TenantNextAction? NextAction);

/// <summary>Where the tenant stands, in the two orthogonal axes the lifecycle actually has.</summary>
public sealed record TenantConfigurationState(
    string Status,
    string? StatusReason,
    string BillingMode,
    string DeploymentProfile,
    string? PlanCode,
    string OffboardingStage,
    bool LegalHoldActive,
    DateTime? TrialEndsOn,
    DateTime? ContractEndOn);

/// <summary>
/// One authority-scoped group of settings, with the fields flattened for display and a flag
/// saying whether this caller may change them.
/// </summary>
/// <param name="Key">Stable slice id: identity, operating, commercial, modules, deployment.</param>
/// <param name="Label">What a salesperson would call this group.</param>
/// <param name="Editable">Whether the CALLER's platform role may write this slice.</param>
/// <param name="RequiredAuthority">Who may, stated for the console to print when Editable is false.</param>
/// <param name="Endpoint">
/// The endpoint that writes this slice. Returned rather than hard-coded in the console so that a
/// screen merging five slices cannot drift from the five endpoints that actually own them.
/// </param>
public sealed record TenantConfigurationSlice(
    string Key,
    string Label,
    bool Editable,
    string RequiredAuthority,
    string Endpoint,
    IReadOnlyList<TenantConfigurationField> Fields);

/// <param name="Derived">
/// True when the value is not keyboard input — it comes from the plan, the country or the
/// deployment. The console renders these read-only with <paramref name="Source"/> as the "why",
/// which is the single largest reduction in fields an operator has to answer.
/// </param>
public sealed record TenantConfigurationField(
    string Key,
    string Label,
    string? Value,
    bool Derived = false,
    string? Source = null);

/// <summary>
/// Something standing between this customer and working software, in business words, with the
/// authority that can clear it and where that is done.
/// </summary>
/// <param name="Owner">
/// Who has to act — Sales, Finance, Engineering or Owner. The activation policy already knows
/// which ROLE may clear each control; this translates that into the person a rep should go and
/// ask, because "requires OwnerMfa" is not an answer a salesperson can act on.
/// </param>
public sealed record TenantConfigurationBlocker(
    string Code,
    string Title,
    string Detail,
    string Owner,
    string? ResolveSlice);

/// <summary>The one thing to do next, so a list row and a header can say it in a sentence.</summary>
public sealed record TenantNextAction(string Label, string Detail, string? Slice);

/// <summary>
/// One row of the customer list: enough to decide whether to open it, and never enough to
/// require opening it to find out.
///
/// <para>The tenant list used to show name, country, plan, billing mode, trial end, status and
/// created date — seven columns, none of which answers "does this one need me today". An operator
/// found that out by opening each customer in turn and reading a twelve-tab screen. The
/// <see cref="NextAction"/> here is the same sentence the customer screen shows, computed once on
/// the server, so a list row and the page behind it can never disagree.</para>
/// </summary>
public sealed record CustomerListRow(
    long TenantId,
    string Name,
    string? LegalName,
    string? CountryCode,
    string Status,
    string BillingMode,
    string? PlanCode,
    DateTime? TrialEndsOn,
    DateTime? ContractEndOn,
    DateTime CreatedOn,
    int BlockerCount,
    /// <summary>Who has to act on the first blocker — Finance, Sales or Support, Owner.</summary>
    string? BlockedOn,
    TenantNextAction? NextAction,
    /// <summary>True when this row is one an operator should look at today.</summary>
    bool NeedsAttention);
