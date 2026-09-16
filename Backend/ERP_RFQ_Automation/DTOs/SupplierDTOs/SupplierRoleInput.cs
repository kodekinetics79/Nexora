using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    /// <summary>
    /// The supplier's role as a person types it, made canonical the way <see cref="SupplierTierInput"/>
    /// does for the tier: "distributor" and "DISTRIBUTOR" are one value, an unknown word is refused at
    /// the API edge with the permitted words named, and blank means "nobody has said".
    /// </summary>
    public static class SupplierRoleInput
    {
        public const int MaximumLength = 32;

        public static string PermittedValues => string.Join(", ", SupplierRoles.All);

        public static bool TryCanonicalize(string? value, string fieldLabel, out string? role, out string? error)
        {
            role = SupplierRoles.Normalize(value);
            error = null;
            if (role is null || SupplierRoles.IsValid(role)) return true;

            error = $"{fieldLabel} '{value!.Trim()}' is not a recognised supplier role. " +
                    $"Use one of {PermittedValues}, or leave it blank.";
            role = null;
            return false;
        }

        /// <summary>
        /// "gulfswitchgear.com" and "https://gulfswitchgear.com/" are one website. Stored as an
        /// absolute origin so the internet search can match a hit against it by domain.
        /// </summary>
        public static string? NormalizeWebsite(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var candidate = value.Trim();
            if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.HostNameType != UriHostNameType.Dns)
                return value.Trim();
            var path = uri.AbsolutePath is "/" or "" ? string.Empty : uri.AbsolutePath.TrimEnd('/');
            return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{path}";
        }
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SupplierRoleAttribute : ValidationAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
        {
            var label = validationContext.DisplayName is { Length: > 0 } name ? name : "Role";
            return SupplierRoleInput.TryCanonicalize(value as string, label, out _, out var error)
                ? ValidationResult.Success
                : new ValidationResult(error, [validationContext.MemberName ?? label]);
        }
    }
}
