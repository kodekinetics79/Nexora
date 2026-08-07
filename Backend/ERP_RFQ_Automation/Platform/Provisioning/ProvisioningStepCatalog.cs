namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// What each step is called, where it sits, and — the part that matters operationally — whether
/// re-running it is safe.
///
/// <para><b>Retriability is a property of the step, not of the failure.</b> Seven of the eight
/// steps re-read their own state before they write, so a retry that follows a rolled-back attempt
/// simply does the work again and a retry that follows a COMMITTED attempt finds its rows already
/// there and does nothing. The eighth cannot work that way and is documented below, because a
/// console that offered an identical retry button for all eight would, on that one, quietly
/// create a second live activation link.</para>
/// </summary>
public static class ProvisioningStepCatalog
{
    /// <param name="Code">Stable machine identifier. Appears in audit metadata and retry requests.</param>
    /// <param name="Ordinal">Execution order; also the console's display order.</param>
    /// <param name="Label">Sentence-case operator-facing name.</param>
    /// <param name="IsRetriable">
    /// False only where re-running duplicates rather than repairs. See
    /// <see cref="ProvisioningStepCodes.Invitation"/>.
    /// </param>
    /// <param name="Rationale">Why re-running is or is not safe, in one sentence, for the console's tooltip.</param>
    public sealed record Definition(
        string Code, int Ordinal, string Label, bool IsRetriable, string Rationale);

    private static readonly Definition[] Definitions =
    [
        new(ProvisioningStepCodes.Tenant, 0, "Create tenant record", true,
            "Re-run is a no-op once the tenant id is recorded on the execution; the id is written " +
            "inside the same transaction as the row, so there is no window where one exists " +
            "without the other."),

        new(ProvisioningStepCodes.BusinessUnit, 1, "Create primary business unit", true,
            "Same recorded-id guard as the tenant step."),

        new(ProvisioningStepCodes.LifecycleStatuses, 2, "Seed lifecycle statuses", true,
            "Check-then-insert per status code, so a re-run fills gaps and rewrites nothing."),

        new(ProvisioningStepCodes.AiPolicy, 3, "Create AI processing policy", true,
            "Existence-checked rather than assumed: on PostgreSQL a trigger writes this row " +
            "itself, and inserting a second one violates the primary key."),

        new(ProvisioningStepCodes.FoundingRole, 4, "Create founding administrator role", true,
            "Check-then-insert on (business unit, SUPER_ADMIN)."),

        new(ProvisioningStepCodes.FoundingAdmin, 5, "Create founding administrator", true,
            "Check-then-insert on the globally unique email. If the address has been taken by " +
            "someone else in the meantime the step fails terminally rather than retrying forever."),

        new(ProvisioningStepCodes.BaselineSeed, 6, "Seed workspace baseline", true,
            "The seeder is idempotent by check-then-insert and never overwrites, so a re-run long " +
            "after go-live fills gaps without restoring a default over something the customer edited."),

        // The one that is NOT re-runnable on its own terms, and the reason the compensation seam
        // exists at all.
        //
        // IssueAsync mints a fresh 256-bit token and a fresh row every time it is called. Calling
        // it again after a partial failure would leave TWO live activation links for the same
        // account, in two different states of the mail system, either of which sets the founding
        // administrator's password. That is a second key to the front door, created by a button
        // labelled "retry".
        //
        // So the step never merely repeats itself: it supersedes first. Every live invitation for
        // that administrator is revoked and exactly one replacement is issued, in the same
        // transaction, so there is no instant at which two links work. That compensation is what
        // makes IsRetriable true here — the flag describes the step's behaviour, not the raw
        // service call underneath it.
        new(ProvisioningStepCodes.Invitation, 7, "Issue activation invitation", true,
            "Never re-issued blindly: the step revokes every live invitation for this " +
            "administrator and issues exactly one replacement, in one transaction, so two working " +
            "links can never coexist. The new link is emailed and is never returned in an API " +
            "response — the original submit was the only moment a link could reach the operator.")
    ];

    private static readonly Dictionary<string, Definition> ByCode =
        Definitions.ToDictionary(definition => definition.Code, StringComparer.Ordinal);

    public static IReadOnlyList<Definition> All => Definitions;

    public static Definition For(string code)
        => ByCode.TryGetValue(code, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown provisioning step.");

    public static bool TryGet(string? code, out Definition definition)
    {
        if (code is not null && ByCode.TryGetValue(code, out var found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }
}
