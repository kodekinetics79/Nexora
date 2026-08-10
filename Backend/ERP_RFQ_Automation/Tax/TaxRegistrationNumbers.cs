using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Tax;

/// <summary>
/// The one definition of a tax registration number in this platform: how it is canonicalised,
/// and when a value is well-formed.
///
/// <para><b>Why this exists.</b> Excluding recoverable supplier input VAT from landed cost
/// (<c>LandedCostFormula.CostBearingTax</c>, decision R15/R18) is not a costing preference — it
/// implicitly books a reclaim against ZATCA. A reclaim requires a valid supplier tax invoice, and
/// a tax invoice requires the supplier's VAT registration number and our own. Until this type
/// existed the platform stored neither, so the reclaim position rested on nothing an auditor could
/// look at.</para>
///
/// <para><b>KSA format.</b> A Saudi VAT registration number is exactly 15 digits, begins with 3 and
/// ends with 3 (the leading 3 is the country/entity marker, the trailing 3 the VAT-group marker).
/// A malformed one is worse than an absent one: it looks like evidence and is not.</para>
///
/// <para><b>Why foreign values are still accepted.</b> A supplier in Germany, the UK or China has
/// no Saudi TRN and never will; refusing every non-Saudi string would make the field unusable on
/// exactly the import lines where the tax treatment is most delicate. So the KSA rule is applied
/// only to values that CLAIM to be Saudi. The claim test is deliberately narrow and mechanical: an
/// all-digit value beginning with 3. That is the shape only a Saudi TRN has — a UAE TRN is also 15
/// digits but begins with 1, and EU/UK numbers carry a country prefix — so a foreign identifier is
/// not swept into the Saudi rule by accident.</para>
///
/// <para>The same predicate is enforced a second time by a database CHECK constraint on PostgreSQL
/// (see <c>ErpRfqAutomationContext.TaxRegistration.cs</c>), so a value that reaches the column by
/// any route other than these validators is still refused.</para>
/// </summary>
public static class TaxRegistrationNumbers
{
    /// <summary>Storage length of the canonical form. Comfortably above every national format.</summary>
    public const int MaxLength = 50;

    /// <summary>Shortest value we will accept at all; below this it is a typo, not a registration.</summary>
    public const int MinLength = 5;

    /// <summary>Digit count of a Saudi VAT registration number.</summary>
    public const int SaudiLength = 15;

    /// <summary>
    /// Canonical storage form: uppercased, with the separators humans type (spaces, hyphens,
    /// non-breaking spaces) removed, so "300-000-000-000-003" and "300 000 000 000 003" are the
    /// same registration and compare equal. Returns null for null/blank, which is how "not
    /// captured" is represented — never an empty string.
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

    /// <summary>
    /// True when the canonical value is claiming to be a Saudi VAT registration number: all
    /// digits, first digit 3. See the type remarks for why the test is this and not "15 digits".
    /// </summary>
    public static bool IsSaudiClaim(string canonical)
        => canonical.Length > 0 && canonical[0] == '3' && canonical.All(char.IsAsciiDigit);

    /// <summary>True for a well-formed Saudi VAT registration number.</summary>
    public static bool IsWellFormedSaudi(string canonical)
        => canonical.Length == SaudiLength && canonical[0] == '3' && canonical[^1] == '3'
           && canonical.All(char.IsAsciiDigit);

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
        if (!canonical.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '/'))
        {
            error = $"{fieldLabel} may contain only letters, digits, '.' and '/' " +
                    "(spaces and hyphens are removed automatically).";
            return false;
        }
        if (IsSaudiClaim(canonical) && !IsWellFormedSaudi(canonical))
        {
            error = $"{fieldLabel} looks like a Saudi VAT registration number but is not well-formed. " +
                    "A KSA VAT number is exactly 15 digits, beginning with 3 and ending with 3. " +
                    "For a non-Saudi registration, enter it with its country prefix.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Model-binding guard for the tax-registration fields on the supplier and business-unit request
/// DTOs, so a malformed value is refused at the API edge with the same message the bulk importer
/// and the service layer use.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class TaxRegistrationNumberAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var label = validationContext.DisplayName is { Length: > 0 } name
            ? name
            : "Tax registration number";
        return TaxRegistrationNumbers.TryCanonicalize(value as string, label, out _, out var error)
            ? ValidationResult.Success
            : new ValidationResult(error, [validationContext.MemberName ?? label]);
    }
}
