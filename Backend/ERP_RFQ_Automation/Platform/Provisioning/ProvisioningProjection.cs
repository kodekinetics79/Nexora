using System.Text.Json;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Turns persisted provisioning state into the shapes the console and the compatibility endpoint
/// consume.
///
/// <para>The second half of this class is the CUTOVER seam: it rebuilds the existing
/// <see cref="ProvisionTenantResponse"/> from a finished execution, so
/// <c>POST /api/platform/tenants</c> can be reimplemented over the durable engine without its
/// body, its status code or its DTO changing. That is what makes the migration a small diff in
/// the controller rather than a rewrite of everything that calls it.</para>
/// </summary>
public static class ProvisioningProjection
{
    public static ProvisioningExecutionDto ToDto(ProvisioningExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var steps = execution.Steps
            .OrderBy(step => step.Ordinal)
            .Select(step =>
            {
                ProvisioningStepCatalog.TryGet(step.StepCode, out var definition);
                return new ProvisioningStepDto
                {
                    Step = step.StepCode,
                    Label = definition?.Label ?? step.StepCode,
                    Ordinal = step.Ordinal,
                    Status = step.Status.ToString(),
                    AttemptCount = step.AttemptCount,
                    StartedOn = step.StartedOn,
                    CompletedOn = step.CompletedOn,
                    DurationMs = step.DurationMs,
                    FailureCode = step.FailureCode,
                    FailureReason = step.FailureReason,
                    Detail = step.Detail,
                    IsRetriable = definition?.IsRetriable ?? true
                };
            })
            .ToList();

        // Skipped counts as completed, and that is not a fudge: a step that does not apply to
        // this request will never move again, so a progress bar that left it outstanding would
        // sit at seven-eighths forever on every password-activation tenant.
        var completed = steps.Count(step =>
            step.Status is nameof(ProvisioningStepStatus.Succeeded)
                or nameof(ProvisioningStepStatus.Skipped));

        return new ProvisioningExecutionDto
        {
            Id = execution.Id,
            State = execution.State.ToString(),
            Slug = execution.Slug,
            Name = execution.Name,
            AdminEmail = execution.AdminEmail,
            AdminActivation = execution.AdminActivation,
            CurrentStep = execution.CurrentStep,
            FailedStep = execution.FailedStep,
            FailureReason = execution.FailureReason,
            FailureIsTerminal = execution.FailureIsTerminal,
            TenantId = execution.TenantId,
            ProvisionedBusinessUnitId = execution.ProvisionedBusinessUnitId,
            FoundingUserId = execution.FoundingUserId,
            CorrelationId = execution.CorrelationId,
            RequestedBy = execution.RequestedBy,
            CreatedOn = execution.CreatedOn,
            StartedOn = execution.StartedOn,
            CompletedOn = execution.CompletedOn,
            AttemptCount = execution.AttemptCount,
            CancelledBy = execution.CancelledBy,
            CancellationReason = execution.CancellationReason,
            Steps = steps,
            CompletedStepCount = completed,
            TotalStepCount = steps.Count
        };
    }

    public static ProvisioningDraftDto ToDto(ProvisioningDraft draft, bool includePayload)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new ProvisioningDraftDto
        {
            Id = draft.Id,
            Name = draft.Name,
            OwnerEmail = draft.OwnerEmail,
            CreatedOn = draft.CreatedOn,
            UpdatedOn = draft.UpdatedOn,
            Version = draft.Version,
            SubmittedExecutionId = draft.SubmittedExecutionId,
            Payload = includePayload && draft.Payload.Length > 0
                ? ProvisioningRequestCanonicalizer.Rehydrate(draft.Payload)
                : null
        };
    }

    // ---- the cutover seam -----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the legacy provisioning response from a COMPLETED execution.
    ///
    /// <para>Every field the old endpoint returned is reconstructible from persisted state except
    /// the two that were only ever in flight: the generated password and the activation URL.
    /// Those are passed in by the caller, which is the synchronous path holding the runner's
    /// in-memory outcome — the only place they exist and the only place they should.</para>
    /// </summary>
    /// <param name="execution">A finished execution, with its steps loaded.</param>
    /// <param name="tenant">The tenant row with its Plan included, read after completion.</param>
    /// <param name="generatedPassword">In-memory only; null on the invite path and on a replay.</param>
    /// <param name="invitation">The runner's in-memory issued invitation, when there was one.</param>
    /// <param name="invitationEmailSent">Whether the configured provider accepted the message.</param>
    /// <param name="planCode">Resolved plan code, or null when no plan is assigned.</param>
    /// <param name="rateCardCode">Resolved pinned rate card code, or null when none is pinned.</param>
    /// <param name="warnings">The revenue-risk lines the legacy endpoint computes.</param>
    public static ProvisionTenantResponse ToLegacyResponse(
        ProvisioningExecution execution,
        TenantSummaryDto tenant,
        string? generatedPassword,
        IssuedTenantAdminInvitation? invitation,
        bool invitationEmailSent,
        string? planCode,
        string? rateCardCode,
        List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(tenant);

        var baseline = ReadBaseline(execution);

        return new ProvisionTenantResponse
        {
            Tenant = tenant,
            FoundingAdmin = new FoundingAdminDto
            {
                UserId = execution.FoundingUserId ?? 0,
                Email = execution.AdminEmail,
                RoleName = "Super Administrator",
                GeneratedPassword = generatedPassword,
                Invitation = invitation is null ? null : new AdminInvitationDto
                {
                    ExpiresAtUtc = invitation.ExpiresAtUtc,
                    // Withheld when the mail went out, matching the legacy contract exactly: the
                    // operator gets the link only in the case where nobody else has it.
                    ActivationUrl = invitationEmailSent ? null : invitation.ActivationUrl,
                    EmailSent = invitationEmailSent
                }
            },
            Baseline = new TenantBaselineDto
            {
                QuoteConfiguration = baseline.QuoteConfigurationCreated,
                BaseCurrency = baseline.BaseCurrencyCode,
                UnitsOfMeasure = baseline.UnitsOfMeasureCreated,
                Roles = baseline.RolesCreated,
                PermissionGrants = baseline.RolePermissionsCreated,
                LeadReferencePrefix = baseline.LeadReferencePrefix
            },
            Billing = new TenantBillingPostureDto
            {
                Mode = tenant.BillingMode,
                PlanCode = planCode,
                RateCardCode = rateCardCode,
                BillingStartsOn = tenant.BillingStartsOn,
                TrialEndsOn = tenant.TrialEndsOn,
                Warnings = warnings
            }
        };
    }

    /// <summary>
    /// The baseline seeder's own report, read back out of the step's <c>Detail</c>.
    ///
    /// <para>This is why steps record what they produced rather than just that they finished: the
    /// legacy response quotes the seeder's counts back to the operator as a readiness checklist,
    /// and without the journal the only way to answer "how many units of measure were created?"
    /// after the fact would be to count them — which would report the customer's edits as
    /// provisioning's work.</para>
    /// </summary>
    public static BaselineFacts ReadBaseline(ProvisioningExecution execution)
    {
        var detail = execution.Steps
            .FirstOrDefault(step => step.StepCode == ProvisioningStepCodes.BaselineSeed)?.Detail;
        if (string.IsNullOrEmpty(detail))
            return new BaselineFacts(false, null, 0, 0, 0, string.Empty);

        try
        {
            using var document = JsonDocument.Parse(detail);
            var root = document.RootElement;
            return new BaselineFacts(
                QuoteConfigurationCreated: Bool(root, "QuoteConfigurationCreated"),
                BaseCurrencyCode: String(root, "BaseCurrencyCode"),
                UnitsOfMeasureCreated: Int(root, "UnitsOfMeasureCreated"),
                RolesCreated: Int(root, "RolesCreated"),
                RolePermissionsCreated: Int(root, "RolePermissionsCreated"),
                LeadReferencePrefix: String(root, "LeadReferencePrefix") ?? string.Empty);
        }
        catch (JsonException)
        {
            // A malformed detail blob must not turn a successful provision into a 500. The counts
            // are evidence for a human, not an invariant anything depends on.
            return new BaselineFacts(false, null, 0, 0, 0, string.Empty);
        }
    }

    public sealed record BaselineFacts(
        bool QuoteConfigurationCreated,
        string? BaseCurrencyCode,
        int UnitsOfMeasureCreated,
        int RolesCreated,
        int RolePermissionsCreated,
        string LeadReferencePrefix);

    private static bool Bool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
           && value.GetBoolean();

    private static int Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
