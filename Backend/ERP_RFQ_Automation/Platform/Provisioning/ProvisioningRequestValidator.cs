using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Everything that can be judged about a provisioning request before any row is written.
///
/// <para><b>Why this is a copy of the rules in <c>TenantsController</c> rather than a call into
/// them.</b> Those rules are private statics on a controller that is, for now, the compatibility
/// surface: its behaviour and its tests must not move underneath it while the durable path is
/// being adopted. The cutover collapses the two — the controller ends up calling this — and until
/// then the duplication is deliberate and bounded to this file, with
/// <c>ProvisioningValidationParityTests</c> holding the two copies to the same verdicts.</para>
///
/// <para><b>These rules run at SUBMIT, not at step time.</b> An operator who mistyped a currency
/// code must be told while the form is still open, not by a step failing four seconds later
/// against a tenant that already exists. Everything here is decidable from the request alone;
/// anything needing the database (is the plan still active, is the address still free) is
/// re-checked by the step that depends on it, because minutes can pass in between.</para>
/// </summary>
public static class ProvisioningRequestValidator
{
    /// <summary>
    /// Mirrors the floor the provisioning wizard enforces. Kept in sync deliberately: a rule that
    /// lives only in the client is a rule any other caller can ignore.
    /// </summary>
    public const int MinimumBillingModeReasonLength = 15;

    /// <summary>Null when the request is acceptable; otherwise the operator-facing reason.</summary>
    public static string? Validate(ProvisionTenantRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ValidateCompanyProfile(request)
               ?? ValidateCommercialTerms(request, ResolveBillingMode(request), nowUtc);
    }

    /// <summary>
    /// Parses the billing mode, defaulting to <see cref="TenantBillingMode.Billable"/>. An
    /// unrecognised value is reported by <see cref="Validate"/> rather than silently defaulting:
    /// a caller who sent "trial" and got "Billable" would find out at the first invoice.
    /// </summary>
    public static TenantBillingMode ResolveBillingMode(ProvisionTenantRequest request)
        => Normalize(request.BillingMode) is string requested
           && Enum.TryParse<TenantBillingMode>(requested, ignoreCase: true, out var parsed)
            ? parsed
            : TenantBillingMode.Billable;

    public static string? ValidateBillingModeSyntax(ProvisionTenantRequest request)
        => Normalize(request.BillingMode) is string requested
           && !Enum.TryParse<TenantBillingMode>(requested, ignoreCase: true, out _)
            ? $"billingMode '{requested}' is not recognised; use one of " +
              $"{string.Join(", ", Enum.GetNames<TenantBillingMode>())}."
            : null;

    /// <summary>Resolved activation method, lowercased. Defaults to the invite path.</summary>
    public static string ResolveActivation(ProvisionTenantRequest request)
        => Normalize(request.AdminActivation)?.ToLowerInvariant() ?? AdminActivationMethods.Invite;

    /// <summary>
    /// The commercial half. These rules only make sense as a set: what a tenant is charged, from
    /// when, and against which price list.
    ///
    /// <para><b>Every rule here closes a measured revenue leak.</b> A Billable tenant with no plan
    /// produces a statement with no base-subscription line, because
    /// <c>BillingStatementService.BuildLines</c> emits that line only when a plan exists — so the
    /// customer is metered and never charged. A Trial with no end date is indistinguishable from
    /// permanent free service and nothing ever prompts conversion. A non-Billable mode with no
    /// written reason is free service that nobody signed for.</para>
    /// </summary>
    public static string? ValidateCommercialTerms(
        ProvisionTenantRequest request, TenantBillingMode mode, DateTime now)
    {
        if (ValidateBillingModeSyntax(request) is string syntaxError)
            return syntaxError;

        if (mode == TenantBillingMode.Billable && request.PlanId is null)
            return "A billable tenant must be assigned a plan: without one its statements carry no " +
                   "base subscription line and the customer is never charged. Assign a plan, or set " +
                   "billingMode to Trial, Internal or Partner with a reason.";

        if (mode != TenantBillingMode.Billable)
        {
            // A length floor, not just presence. An exemption justified as "x" satisfies a
            // required-field check while leaving the paper trail worth nothing — and the whole
            // point of demanding a reason is that somebody has to be able to read it later and
            // understand why this customer was not charged.
            var reason = request.BillingModeReason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length < MinimumBillingModeReasonLength)
                return $"billingMode '{mode}' means this tenant is not charged; billingModeReason " +
                       $"is required and must be at least {MinimumBillingModeReasonLength} characters " +
                       "so the exemption is attributable to a real decision.";
        }

        if (mode == TenantBillingMode.Trial)
        {
            if (request.TrialEndsOn is not DateTime trialEnd)
                return "A trial tenant must carry trialEndsOn. An open-ended trial is free service " +
                       "with no conversion date.";
            if (trialEnd <= now)
                return $"trialEndsOn {trialEnd:yyyy-MM-dd} is not in the future; the trial would be " +
                       "expired the moment it is created.";
        }

        if (request.ContractStartOn is DateTime start && request.ContractEndOn is DateTime end
            && end <= start)
            return "contractEndOn must fall after contractStartOn.";

        return null;
    }

    /// <summary>
    /// Non-commercial field validation. Codes are checked for SHAPE and, where the runtime can
    /// answer authoritatively (time zones), for existence — a mistyped IANA id would otherwise
    /// surface much later as an SLA clock running in the wrong offset.
    /// </summary>
    public static string? ValidateCompanyProfile(ProvisionTenantRequest request)
    {
        if (Normalize(request.CountryCode) is string country
            && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return $"countryCode '{country}' is not an ISO-3166-1 alpha-2 code (two letters, e.g. 'SA').";

        // Required, not optional. Every consequence of a missing base currency is INVISIBLE:
        // FxConversionService cannot resolve a base, so the dashboard total reports itself
        // unavailable; unit costs silently null out; the general ledger refuses to open a book.
        // Provisioning is the last moment at which anyone can still be told.
        if (Normalize(request.BaseCurrencyCode) is not string currency)
            return "baseCurrencyCode is required: without a base currency the tenant's totals, " +
                   "unit costs and general ledger all fail silently rather than visibly.";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            return $"baseCurrencyCode '{currency}' is not an ISO-4217 code (three letters, e.g. 'SAR').";

        if (Normalize(request.TimeZoneId) is string timeZone)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (Exception exception) when (
                exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"timeZoneId '{timeZone}' is not a time zone this server recognises; use an " +
                       "IANA identifier such as 'Asia/Riyadh'.";
            }
        }

        var activation = ResolveActivation(request);
        if (activation is not (AdminActivationMethods.Invite or AdminActivationMethods.Password))
            return $"adminActivation '{activation}' is not recognised; use " +
                   $"'{AdminActivationMethods.Invite}' or '{AdminActivationMethods.Password}'.";

        // Refused rather than ignored. "No password exists until the administrator sets one" is
        // the entire security property of the invite path; silently discarding a supplied one
        // would leave that property resting on a client's good manners, and a caller who sent a
        // password believes it was set.
        if (activation == AdminActivationMethods.Invite && !string.IsNullOrEmpty(request.AdminPassword))
            return "adminPassword cannot be supplied with adminActivation 'invite': the invited " +
                   "administrator chooses their own credential and none exists until they do. " +
                   $"Use adminActivation '{AdminActivationMethods.Password}' to set one directly.";

        return null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The one-time credential minted when an operator chooses the password activation path and
/// supplies none of their own.
/// </summary>
public static class ProvisioningInitialCredential
{
    /// <summary>
    /// Twenty characters from a cryptographic RNG with guaranteed class coverage, over an
    /// alphabet with lookalikes (0/O, 1/l/I) removed because this value gets read aloud or
    /// retyped during customer handover.
    /// </summary>
    public static string Generate()
    {
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^*-_+=";
        const string all = upper + lower + digits + symbols;

        var chars = new char[20];
        chars[0] = upper[System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[System.Security.Cryptography.RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates with the same RNG, so the guaranteed classes are not always positions 0–3.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
