using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// The authenticated commercial data plane. Module permissions answer what a caller may do;
/// this context answers which rows they may do it to. The answer is always derived from the
/// authenticated user and role claims and never from a client supplied owner id or list filter.
/// </summary>
public sealed record CommercialActorScope(
    long BusinessUnitId,
    long UserId,
    long RoleId,
    AccountTeamScope AccountScope);

public interface ICommercialAccessContext
{
    Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default);
    Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default);
    Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default);
    Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default);
    Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default);
    Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default);
}

/// <summary>
/// Request-scoped resolver and direct-id guard. Invalid or incomplete identity fails closed.
/// Out-of-scope records are intentionally indistinguishable from missing records to callers.
/// </summary>
public sealed class CommercialAccessContext : ICommercialAccessContext
{
    private readonly IHttpContextAccessor _http;
    private readonly IAccountTeamScopeResolver _scopeResolver;
    private readonly ErpRfqAutomationContext _db;
    private CommercialActorScope? _resolved;
    private bool _resolutionAttempted;

    public CommercialAccessContext(
        IHttpContextAccessor http,
        IAccountTeamScopeResolver scopeResolver,
        ErpRfqAutomationContext db)
    {
        _http = http;
        _scopeResolver = scopeResolver;
        _db = db;
    }

    public async Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default)
    {
        if (_resolutionAttempted) return _resolved;
        _resolutionAttempted = true;

        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true
            || !TryPositiveLong(user.FindFirstValue("businessUnitId"), out var businessUnitId)
            || !TryPositiveLong(user.FindFirstValue("roleId"), out var roleId)
            || !TryPositiveLong(
                user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId))
        {
            return null;
        }

        var scope = await _scopeResolver.ResolveAsync(
            userId, roleId, businessUnitId, DateTime.UtcNow, ct);
        _resolved = new CommercialActorScope(businessUnitId, userId, roleId, scope);
        return _resolved;
    }

    public async Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default)
    {
        var actor = await ResolveAsync(ct);
        return actor != null && await _db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(_db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .AnyAsync(x => x.Id == leadId, ct);
    }

    public async Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default)
    {
        var actor = await ResolveAsync(ct);
        if (actor == null) return false;
        var query = _db.Customers.AsNoTracking()
            .Where(x => x.Buid == actor.BusinessUnitId);
        if (!actor.AccountScope.IsTenantWide)
            query = query.InAccountScope(
                _db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow);
        return await query.AnyAsync(x => x.Id == customerId, ct);
    }

    public async Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default)
    {
        var actor = await ResolveAsync(ct);
        return actor != null && await _db.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(_db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .AnyAsync(x => x.Id == rfqId, ct);
    }

    public async Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default)
    {
        var actor = await ResolveAsync(ct);
        return actor != null && await _db.Quotes.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(_db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .AnyAsync(x => x.Id == quoteId, ct);
    }

    public async Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default)
    {
        var actor = await ResolveAsync(ct);
        return actor != null && await _db.Orders.AsNoTracking()
            .Where(x => x.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(_db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .AnyAsync(x => x.Id == orderId, ct);
    }

    private static bool TryPositiveLong(string? value, out long parsed) =>
        long.TryParse(value, out parsed) && parsed > 0;
}

/// <summary>
/// One composable definition of commercial row visibility. Callers must apply the tenant
/// predicate first. Unassigned leads are deliberately absent from ordinary record scope: they
/// are discoverable only through the governed routing queue, and claiming one establishes access.
/// </summary>
public static class CommercialAccessFilters
{
    public static IQueryable<Lead> InCommercialScope(
        this IQueryable<Lead> query,
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
    {
        if (scope.IsTenantWide) return query;

        var userIds = scope.UserIds;
        // A Lead is work, not merely account metadata. A named Lead owner is the authority;
        // customer ownership must never let one rep open another rep's opportunity. Owner-null
        // Leads remain in the governed routing queue until an atomic claim establishes access.
        return query.Where(lead =>
            lead.AssignTo != null && userIds.Contains(lead.AssignTo.Value));
    }

    public static IQueryable<Rfq> InCommercialScope(
        this IQueryable<Rfq> query,
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
    {
        if (scope.IsTenantWide) return query;

        var leads = db.Leads
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, scope, asOfUtc)
            .Select(x => x.Id);
        var customers = AccountTeamReadFilter.CustomerIdsInScope(
            db, businessUnitId, scope, asOfUtc)!;

        return query.Where(rfq => rfq.LeadId != null
            ? leads.Contains(rfq.LeadId.Value)
            : rfq.CustomerId != null && customers.Contains(rfq.CustomerId.Value));
    }

    public static IQueryable<Quote> InCommercialScope(
        this IQueryable<Quote> query,
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
    {
        if (scope.IsTenantWide) return query;

        var rfqs = db.Rfqs
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, scope, asOfUtc)
            .Select(x => x.Id);
        var customers = AccountTeamReadFilter.CustomerIdsInScope(
            db, businessUnitId, scope, asOfUtc)!;

        return query.Where(quote => quote.Rfqid != null
            ? rfqs.Contains(quote.Rfqid.Value)
            : quote.CustomerId != null && customers.Contains(quote.CustomerId.Value));
    }

    public static IQueryable<Order> InCommercialScope(
        this IQueryable<Order> query,
        ErpRfqAutomationContext db,
        long businessUnitId,
        AccountTeamScope scope,
        DateTime asOfUtc)
    {
        if (scope.IsTenantWide) return query;

        var leads = db.Leads
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, scope, asOfUtc)
            .Select(x => x.Id);
        var rfqs = db.Rfqs
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, scope, asOfUtc)
            .Select(x => x.Id);
        var quotes = db.Quotes
            .Where(x => x.BusinessUnitId == businessUnitId)
            .InCommercialScope(db, businessUnitId, scope, asOfUtc)
            .Select(x => x.Id);
        var customers = AccountTeamReadFilter.CustomerIdsInScope(
            db, businessUnitId, scope, asOfUtc)!;

        return query.Where(order => order.LeadId != null
            ? leads.Contains(order.LeadId.Value)
            : order.Rfqid != null
                ? rfqs.Contains(order.Rfqid.Value)
                : order.QuoteId != null
                    ? quotes.Contains(order.QuoteId.Value)
                    : customers.Contains(order.CustomerId));
    }
}
