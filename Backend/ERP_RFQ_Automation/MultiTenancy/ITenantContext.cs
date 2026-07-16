using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.MultiTenancy
{
    /// <summary>
    /// Per-request tenant scope, resolved from the authenticated user's
    /// <c>businessUnitId</c> JWT claim. <see cref="BusinessUnitId"/> is null when
    /// there is no tenant context — anonymous requests, login (before a token
    /// exists), and background workers. The EF global query filters treat a null
    /// tenant as "do not filter", so those paths keep working while every
    /// authenticated request is transparently scoped to its business unit.
    /// This flips tenant isolation from opt-in (hand-written .Where) to
    /// opt-out (must consciously IgnoreQueryFilters), closing the whole class
    /// of "forgot the filter" cross-tenant leaks. (ADR-0005)
    /// </summary>
    public interface ITenantContext
    {
        long? BusinessUnitId { get; }
    }

    public sealed class HttpTenantContext : ITenantContext
    {
        public long? BusinessUnitId { get; }

        public HttpTenantContext(IHttpContextAccessor accessor)
        {
            var raw = accessor.HttpContext?.User?.FindFirst("businessUnitId")?.Value;
            if (long.TryParse(raw, out var id) && id > 0)
                BusinessUnitId = id;
        }
    }
}
