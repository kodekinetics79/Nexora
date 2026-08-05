using System.Security.Claims;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// The server's answer to "who is making this request", derived exclusively from the validated
    /// JWT (RC-7).
    ///
    /// The IAM controllers previously attributed writes to
    /// <c>User.Identity?.Name ?? request.CreatedBy ?? "System"</c>. <c>User.Identity.Name</c> reads
    /// the principal's <c>NameClaimType</c>, which the tenant bearer scheme never configured, so it
    /// was ALWAYS null and the client-supplied <c>CreatedBy</c> string always won — a user could
    /// name anyone as the author of their own privilege grant. Nothing here can be influenced by a
    /// request body.
    /// </summary>
    public readonly record struct ActorContext(
        long? UserId,
        long? RoleId,
        long BusinessUnitId,
        string? Email,
        string? CorrelationId)
    {
        /// <summary>True when the token carried a usable tenant. Fail-closed everywhere else.</summary>
        public bool HasTenant => BusinessUnitId > 0;

        /// <summary>
        /// Display name for legacy <c>CreatedBy</c>/<c>ModifiedBy</c> string columns. Prefixed so a
        /// row written after this change is distinguishable from the forgeable values that came
        /// before it.
        /// </summary>
        public string Stamp =>
            UserId is { } id
                ? $"user:{id}" + (string.IsNullOrWhiteSpace(Email) ? string.Empty : $" ({Email})")
                : "System";

        public static ActorContext From(ClaimsPrincipal? principal, string? correlationId = null)
        {
            if (principal is null)
                return new ActorContext(null, null, 0, null, correlationId);

            // "sub" is what AuthRepository.GenerateJwtToken writes; ASP.NET Core's inbound claim
            // mapping rewrites it to ClaimTypes.NameIdentifier under the default configuration, so
            // both spellings have to be probed or the actor silently comes back null.
            var userId = ParseLong(
                principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub"));
            var roleId = ParseLong(principal.FindFirstValue("roleId"));
            var businessUnitId = ParseLong(principal.FindFirstValue("businessUnitId")) ?? 0;
            var email = principal.FindFirstValue(ClaimTypes.Email)
                        ?? principal.FindFirstValue("email");

            return new ActorContext(userId, roleId, businessUnitId, email, correlationId);
        }

        private static long? ParseLong(string? value)
            => long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;
    }
}
