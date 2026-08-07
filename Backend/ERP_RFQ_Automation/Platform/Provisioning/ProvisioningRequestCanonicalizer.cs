using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Turns a submitted request into the two derived values an execution stores: the fingerprint
/// that decides whether a repeat submit is a replay or a conflict, and the redacted payload a
/// resume rebuilds its graph from.
/// </summary>
public static class ProvisioningRequestCanonicalizer
{
    /// <summary>
    /// Property-name-sorted, so two JSON bodies that differ only in field order fingerprint
    /// identically. A client that serialises its form object twice has no obligation to emit the
    /// same order both times, and telling such a caller "different payload" would break the
    /// retry it was trying to perform.
    /// </summary>
    private static readonly JsonSerializerOptions Canonical = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions Storage = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// SHA-256 over the request's meaningful content.
    ///
    /// <para><b>The password is included.</b> A retry that changes the administrator's credential
    /// is a different request and must be refused under a reused key, not silently accepted as a
    /// replay of the first one. Hashing it is safe — the digest covers the entire body, so it
    /// reveals nothing about the credential that a guess could confirm — and it is the only way
    /// the "same key, different payload" rule can be honest about the field that matters most.</para>
    ///
    /// <para>Whitespace-trimmed and lowercased where the server itself normalises, so a caller
    /// who retries with " Acme " after sending "Acme" gets their original result rather than a
    /// conflict over a difference the server was going to erase anyway.</para>
    /// </summary>
    public static string Fingerprint(ProvisionTenantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A flat, explicitly ordered projection rather than a reflective walk of the DTO. Adding
        // a property to ProvisionTenantRequest must be a deliberate decision about whether it
        // changes request identity — a reflective hash would silently answer "yes" and invalidate
        // every in-flight idempotency key the moment the DTO grew a field.
        var canonical = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["name"] = Trim(request.Name),
            ["slug"] = Trim(request.Slug)?.ToLowerInvariant(),
            ["legalName"] = Trim(request.LegalName),
            ["registrationNumber"] = Trim(request.RegistrationNumber),
            ["taxNumber"] = Trim(request.TaxNumber),
            ["countryCode"] = Trim(request.CountryCode)?.ToUpperInvariant(),
            ["industry"] = Trim(request.Industry),
            ["website"] = Trim(request.Website),
            ["addressLine1"] = Trim(request.AddressLine1),
            ["addressLine2"] = Trim(request.AddressLine2),
            ["city"] = Trim(request.City),
            ["stateProvince"] = Trim(request.StateProvince),
            ["postalCode"] = Trim(request.PostalCode),
            ["phone"] = Trim(request.Phone),
            ["contactEmail"] = Trim(request.ContactEmail),
            ["logoUrl"] = Trim(request.LogoUrl),
            ["baseCurrencyCode"] = Trim(request.BaseCurrencyCode)?.ToUpperInvariant(),
            ["timeZoneId"] = Trim(request.TimeZoneId),
            ["locale"] = Trim(request.Locale),
            ["dataRegion"] = Trim(request.DataRegion),
            ["planId"] = request.PlanId?.ToString(),
            ["billingMode"] = Trim(request.BillingMode)?.ToLowerInvariant(),
            ["billingModeReason"] = Trim(request.BillingModeReason),
            ["rateCardId"] = request.RateCardId?.ToString(),
            ["billingStartsOn"] = Date(request.BillingStartsOn),
            ["trialEndsOn"] = Date(request.TrialEndsOn),
            ["contractStartOn"] = Date(request.ContractStartOn),
            ["contractEndOn"] = Date(request.ContractEndOn),
            ["paymentTermsDays"] = request.PaymentTermsDays?.ToString(),
            ["purchaseOrderReference"] = Trim(request.PurchaseOrderReference),
            ["billingContactName"] = Trim(request.BillingContactName),
            ["billingContactEmail"] = Trim(request.BillingContactEmail),
            ["billingAddress"] = Trim(request.BillingAddress),
            ["accountOwnerEmail"] = Trim(request.AccountOwnerEmail),
            ["adminEmail"] = Trim(request.AdminEmail),
            ["adminFirstName"] = Trim(request.AdminFirstName),
            ["adminLastName"] = Trim(request.AdminLastName),
            ["adminJobTitle"] = Trim(request.AdminJobTitle),
            ["adminPhone"] = Trim(request.AdminPhone),
            ["adminActivation"] = Trim(request.AdminActivation)?.ToLowerInvariant(),
            ["adminPassword"] = request.AdminPassword
        };

        var json = JsonSerializer.Serialize(canonical, Canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>
    /// The request as it is stored on the execution, with the credential removed.
    ///
    /// <para>Removed rather than masked. A resume rebuilds the tenant, the business unit and the
    /// administrator's <c>Users</c> row from this payload, and the only thing it needs the
    /// password for is the hash — which is computed once at submit and kept in its own column.
    /// Storing the plaintext as well would put a live customer credential in a table an operator
    /// can read, which is the exact property the invitation flow gave up convenience to avoid.</para>
    /// </summary>
    public static string Redact(ProvisionTenantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var copy = Clone(request);
        copy.AdminPassword = null;
        return JsonSerializer.Serialize(copy, Storage);
    }

    /// <summary>Rehydrates a stored payload. The credential is absent by construction; the runner
    /// uses <c>ProvisioningExecution.AdminPasswordHash</c> instead.</summary>
    public static ProvisionTenantRequest Rehydrate(string payload)
        => JsonSerializer.Deserialize<ProvisionTenantRequest>(payload, Storage)
           ?? throw new InvalidOperationException(
               "The stored provisioning payload could not be read back. The execution cannot be resumed.");

    /// <summary>
    /// A deep-enough copy for redaction and for drafts. Serialise-and-parse rather than a
    /// hand-written clone: the DTO is thirty-odd properties long and a hand-written copy that
    /// silently misses one added later would drop a customer's tax number on resume.
    /// </summary>
    public static ProvisionTenantRequest Clone(ProvisionTenantRequest request)
        => JsonSerializer.Deserialize<ProvisionTenantRequest>(
               JsonSerializer.Serialize(request, Storage), Storage)
           ?? throw new InvalidOperationException("The provisioning request could not be copied.");

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Round-tripped through the invariant "o" format so a client sending "2026-01-01" and one
    // sending "2026-01-01T00:00:00Z" are recognised as the same request.
    private static string? Date(DateTime? value) => value?.ToString("O");
}
