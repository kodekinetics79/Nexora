using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    /// <summary>
    /// The one definition of how a typed or pasted supplier tier is turned into a stored value.
    ///
    /// <para>Three call sites share it — the request DTOs (via <see cref="SupplierTierAttribute"/>),
    /// the controller's create/update assignment, and the bulk importer's per-row parse — so the API
    /// and the spreadsheet cannot disagree about which strings are a tier. The permitted set itself
    /// is never restated here: it is read from <see cref="SupplierTiers.All"/>, which is also what
    /// the database CHECK constraint and the tier select on the supplier form read.</para>
    ///
    /// <para>Canonicalisation is whitespace, hyphen and case only. It does not infer: "Tier 1" is
    /// not silently promoted to <c>TIER_1_PARTNER</c>, because a tier is customer-set master data
    /// and a guessed classification would be indistinguishable from a typed one. Blank is not an
    /// error — null means NOT YET CLASSIFIED, the state every existing supplier is in.</para>
    /// </summary>
    public static class SupplierTierInput
    {
        /// <summary>The permitted values, written out for an operator-facing error message.</summary>
        public static string PermittedValues => string.Join(", ", SupplierTiers.All);

        /// <summary>
        /// Upper bound on what <see cref="Normalize"/> will allocate a stack buffer for. The longest
        /// real tier is 22 characters and the column holds 32; this leaves generous room for spacing
        /// and hyphenation variants while keeping the buffer to a fixed, trivial size.
        /// </summary>
        public const int MaximumCanonicalisableLength = 64;

        /// <summary>
        /// Canonical storage form: uppercased, with spaces and hyphens folded to the underscore the
        /// constants use, so "tier 1 partner" and "Tier-1-Partner" are the same tier. Returns null
        /// for null/blank — "not yet classified" is never an empty string. An unrecognised value is
        /// returned in its canonicalised shape so the caller can name it back to the operator;
        /// use <see cref="TryCanonicalize"/> to find out whether it is a tier at all.
        /// </summary>
        public static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            // Bound BEFORE the stackalloc, not after. This runs inside model binding on a
            // [FromForm] field whose default ASP.NET Core value limit is 4 MB, so an unbounded
            // `stackalloc char[value.Length]` was an ~8 MB allocation on a 1 MB request-thread
            // stack — a StackOverflowException, which no catch block can intercept and which takes
            // the worker process down rather than returning an error. The column is 32 characters;
            // anything appreciably longer is not a tier by any canonicalisation and is refused here
            // rather than allocated for.
            if (value.Length > MaximumCanonicalisableLength) return null;

            Span<char> buffer = stackalloc char[value.Length];
            var length = 0;
            foreach (var character in value.Trim())
            {
                buffer[length++] = char.IsWhiteSpace(character) || character is '-' or '–' or '—'
                    ? '_'
                    : char.ToUpperInvariant(character);
            }
            return length == 0 ? null : new string(buffer[..length]);
        }

        /// <summary>
        /// Validates a captured tier. <paramref name="tier"/> is the value to store — null when the
        /// field was left empty, which is always allowed. Returns false with an operator-facing
        /// <paramref name="error"/> that names both the rejected value and the permitted set,
        /// because "invalid tier" alone leaves the operator guessing at the spelling.
        /// </summary>
        public static bool TryCanonicalize(string? value, string fieldLabel,
            out string? tier, out string? error)
        {
            tier = Normalize(value);
            error = null;
            if (tier is null || SupplierTiers.IsValid(tier)) return true;

            error = $"{fieldLabel} '{value!.Trim()}' is not a recognised supplier tier. " +
                    $"Use one of {PermittedValues}, or leave it blank for a supplier that is not yet classified.";
            tier = null;
            return false;
        }
    }

    /// <summary>
    /// Model-binding guard for the tier field on the supplier request DTOs, so an unknown tier is
    /// refused at the API edge with the same message the bulk importer produces. Shaped exactly like
    /// <see cref="ERP_RFQ_Automation.Tax.TaxRegistrationNumberAttribute"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SupplierTierAttribute : ValidationAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            var label = validationContext.DisplayName is { Length: > 0 } name ? name : "Tier";
            return SupplierTierInput.TryCanonicalize(value as string, label, out _, out var error)
                ? ValidationResult.Success
                : new ValidationResult(error, [validationContext.MemberName ?? label]);
        }
    }
}
