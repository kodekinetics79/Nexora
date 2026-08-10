using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>
/// The closed sector list FR-CST-01 names: Government, Semi-Government, Private.
///
/// <para>Stored as the CODE, never the label. A tenant that renames "Semi-Government" on screen
/// must not orphan every row that was classified under the old wording, and a report that groups by
/// sector must group by something a rename cannot move.</para>
///
/// <para>There is no <c>OTHER</c> and no <c>UNKNOWN</c> member on purpose. "Not classified" is
/// represented by a NULL column, which is a different fact from "classified as other" and reads as
/// a gap on screen instead of as an answer.</para>
/// </summary>
public static class CustomerSectors
{
    public const string Government = "GOVERNMENT";
    public const string SemiGovernment = "SEMI_GOVERNMENT";
    public const string Private = "PRIVATE";

    public const int MaxLength = 20;

    /// <summary>The stored values, in the order they are offered.</summary>
    public static readonly string[] All = [Government, SemiGovernment, Private];

    /// <summary>Operator-facing label for a stored code. Presentation only — never stored.</summary>
    public static string Describe(string? code) => code switch
    {
        Government => "Government",
        SemiGovernment => "Semi-Government",
        Private => "Private",
        _ => "Not classified"
    };

    /// <summary>
    /// Canonicalises what a caller sent. Accepts the code in any casing and with spaces or hyphens
    /// where the underscore belongs ("Semi Government", "semi-government"), because those are what
    /// a person types and an importer emits. Returns null for null/blank — "not classified".
    /// </summary>
    public static bool TryCanonicalize(string? value, out string? canonical, out string? error)
    {
        canonical = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var normalized = string.Join('_', value.Trim().ToUpperInvariant()
            .Split([' ', '-', '/'], StringSplitOptions.RemoveEmptyEntries));

        if (Array.IndexOf(All, normalized) < 0)
        {
            error = "Customer sector must be one of Government, Semi-Government or Private. " +
                    "Leave it empty if the sector has not been established — it is recorded as " +
                    "'not classified' rather than assumed to be private.";
            return false;
        }

        canonical = normalized;
        return true;
    }
}

/// <summary>
/// The one definition of a commercial registration ("CR") number in this platform.
///
/// <para><b>KSA format.</b> A Saudi commercial registration is exactly 10 digits. A malformed one
/// is worse than an absent one: a CR number is the identifier a counterparty is verified against,
/// and a value that looks like evidence and is not is how an unverified counterparty passes review.</para>
///
/// <para><b>Why foreign values are still accepted, and how.</b> A customer registered outside Saudi
/// Arabia has no CR number and never will. Unlike a VAT number there is no digit that marks a
/// Saudi CR apart, so the claim test is the only mechanical one available: an ALL-DIGIT value is
/// treated as claiming to be Saudi and must therefore be exactly 10 digits. A foreign registration
/// is recorded with its country prefix ("GB01234567"), which is the convention the tax-registration
/// validator already states in its own error message — the two fields behave the same way, on
/// purpose, so an operator learns one rule rather than two.</para>
///
/// <para>The same predicate is enforced a second time by a database CHECK constraint on PostgreSQL
/// (see <c>ErpRfqAutomationContext.CustomerMaster.cs</c>), so a value that reaches the column by
/// any route other than this validator is still refused.</para>
/// </summary>
public static class CommercialRegistrationNumbers
{
    /// <summary>Storage length of the canonical form.</summary>
    public const int MaxLength = 30;

    /// <summary>Shortest value accepted at all; below this it is a typo, not a registration.</summary>
    public const int MinLength = 5;

    /// <summary>Digit count of a Saudi commercial registration number.</summary>
    public const int SaudiLength = 10;

    /// <summary>
    /// Canonical storage form: uppercased with the separators humans type removed, so
    /// "1010-123456" and "1010 123456" are the same registration and compare equal. Null for
    /// null/blank — "not captured", never an empty string.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '-' or '–' or '—') continue;
            buffer[length++] = char.ToUpperInvariant(character);
        }
        return length == 0 ? null : new string(buffer[..length]);
    }

    /// <summary>True when the canonical value is claiming to be a Saudi CR: all digits.</summary>
    public static bool IsSaudiClaim(string canonical)
        => canonical.Length > 0 && canonical.All(char.IsAsciiDigit);

    /// <summary>True for a well-formed Saudi commercial registration number.</summary>
    public static bool IsWellFormedSaudi(string canonical)
        => canonical.Length == SaudiLength && canonical.All(char.IsAsciiDigit);

    /// <summary>
    /// Validates a captured value. <paramref name="canonical"/> is the form to store (null when the
    /// field was left empty, which is always allowed). Returns false with an operator-facing
    /// <paramref name="error"/> naming what is wrong.
    /// </summary>
    public static bool TryCanonicalize(string? value, string fieldLabel,
        out string? canonical, out string? error)
    {
        canonical = Normalize(value);
        error = null;
        if (canonical is null) return true;

        if (canonical.Length > MaxLength)
        {
            error = $"{fieldLabel} is longer than {MaxLength} characters.";
            return false;
        }
        if (canonical.Length < MinLength)
        {
            error = $"{fieldLabel} is too short to be a registration number.";
            return false;
        }
        if (!canonical.All(char.IsAsciiLetterOrDigit))
        {
            error = $"{fieldLabel} may contain only letters and digits " +
                    "(spaces and hyphens are removed automatically).";
            return false;
        }
        if (IsSaudiClaim(canonical) && !IsWellFormedSaudi(canonical))
        {
            error = $"{fieldLabel} looks like a Saudi commercial registration number but is not " +
                    $"well-formed. A KSA CR number is exactly {SaudiLength} digits. " +
                    "For a non-Saudi registration, enter it with its country prefix.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Model-binding guard for the CR field on the customer request DTOs, so a malformed value is
/// refused at the API edge with the same message the service layer uses. Mirrors
/// <c>ERP_RFQ_Automation.Tax.TaxRegistrationNumberAttribute</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class CommercialRegistrationNumberAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var label = validationContext.DisplayName is { Length: > 0 } name
            ? name
            : "Commercial registration number";
        return CommercialRegistrationNumbers.TryCanonicalize(value as string, label, out _, out var error)
            ? ValidationResult.Success
            : new ValidationResult(error, [validationContext.MemberName ?? label]);
    }
}

/// <summary>
/// Model-binding guard for the customer sector field.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class CustomerSectorAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        => CustomerSectors.TryCanonicalize(value as string, out _, out var error)
            ? ValidationResult.Success
            : new ValidationResult(error, [validationContext.MemberName ?? "Sector"]);
}
