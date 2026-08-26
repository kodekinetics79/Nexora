using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Search;

public interface IGlobalSearchService
{
    Task<GlobalSearchResponse> SearchAsync(
        GlobalSearchRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Query">The raw term. Blank is refused by the controller, not silently widened.</param>
/// <param name="Entities">Which families to search; null or empty means all of them.</param>
/// <param name="From">Inclusive lower bound of the date filter.</param>
/// <param name="To">EXCLUSIVE upper bound, matching every other window in this codebase
/// (<c>[from, to)</c>). An inclusive upper bound is how a "to 30 June" filter silently drops
/// everything recorded during 30 June.</param>
/// <param name="Status">Status wording to filter on, matched against the status row's code and its
/// display value, case-insensitively and as an EXACT match after trimming — a substring match on a
/// status turns "OPEN" into a filter that also selects "REOPENED".</param>
public sealed record GlobalSearchRequest(
    string Query,
    IReadOnlyCollection<string>? Entities,
    DateTime? From,
    DateTime? To,
    string? Status,
    int PerEntityLimit,
    long BusinessUnitId,
    long UserId,
    long RoleId,
    AccountTeamScope Scope);

/// <summary>
/// FR-DSH-04 — one term, searched across the commercial entities, with the date-range and status
/// filters the requirement names.
///
/// <para><b>What this replaces.</b> The top bar held a ten-entry keyword→route table in
/// <c>Navbar.tsx</c>. It made no network request, so it could not find a record; and an unmatched
/// term navigated to <c>/dashboard</c>, so a search for a customer name that does not exist looked
/// exactly like a search for one that does. That is wiring-contract failure #7 — a control that
/// reports success while doing nothing.</para>
///
/// <para><b>Permissions are per family, not per request.</b> Each family is read under its own
/// module permission, resolved the same way <see cref="PermissionHandler"/> resolves it: rank
/// <see cref="RoleRanks.Admin"/> and above satisfies it, everything below is decided by an explicit
/// RolePermissions row and is fail-closed. A family the caller may not read is reported in
/// <see cref="GlobalSearchResponse.DeniedEntities"/> rather than dropped, because a silently
/// shorter answer is indistinguishable from "nothing matched".</para>
///
/// <para><b>Commercial families are row-scoped.</b> Customers run through
/// <see cref="AccountTeamReadFilter"/> and Lead → RFQ → Quote → Order → Shipment run through the
/// same inherited commercial scope as their detail APIs. Quick search cannot become the way to
/// enumerate a colleague's pipeline.</para>
///
/// <para><b>Matching.</b> Case-insensitive CONTAINS on the identifying columns of each family. It
/// is deliberately not a ranked full-text search: PostgreSQL <c>tsvector</c> indexes, a stemmer and
/// a language configuration are a schema change and a tuning exercise, and shipping an unranked
/// substring search that states its own limit is better than shipping a relevance score nobody can
/// explain. The limit is stated in the gate report, not hidden here.</para>
/// </summary>
public sealed class GlobalSearchService : IGlobalSearchService
{
    /// <summary>Shortest term accepted. One character matches most of the estate and the result is
    /// a slow query that tells the user nothing.</summary>
    public const int MinimumQueryLength = 2;

    public const int MaxPerEntityLimit = 25;
    public const int DefaultPerEntityLimit = 5;

    private readonly ErpRfqAutomationContext _db;
    private readonly IRolePermissionRepository _permissions;
    private readonly IRoleGate _roleGate;

    public GlobalSearchService(
        ErpRfqAutomationContext db, IRolePermissionRepository permissions, IRoleGate roleGate)
    {
        _db = db;
        _permissions = permissions;
        _roleGate = roleGate;
    }

    public async Task<GlobalSearchResponse> SearchAsync(
        GlobalSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var term = (request.Query ?? string.Empty).Trim();
        if (term.Length < MinimumQueryLength)
            throw new ArgumentException(
                $"Enter at least {MinimumQueryLength} characters to search.", nameof(request));

        var bu = request.BusinessUnitId;
        if (bu <= 0) throw new ArgumentException("An authenticated business unit is required.");

        var limit = Math.Clamp(
            request.PerEntityLimit <= 0 ? DefaultPerEntityLimit : request.PerEntityLimit,
            1, MaxPerEntityLimit);

        var requested = (request.Entities is { Count: > 0 }
                ? request.Entities.Select(e => e.Trim().ToLowerInvariant())
                        .Where(e => SearchEntities.All.Contains(e))
                : SearchEntities.All)
            .Distinct()
            .ToArray();

        if (requested.Length == 0)
            throw new ArgumentException("No recognised entity was requested.", nameof(request));

        var pattern = term.ToLowerInvariant();
        var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();

        var searched = new List<string>();
        var denied = new List<string>();
        var truncated = new List<string>();
        var notes = new List<string>();
        var hits = new List<SearchHit>();

        // The status vocabulary, resolved ONCE. Status lives on SetupMaster rows keyed by id from
        // several tables, so filtering by wording means resolving the wording to ids first — and
        // resolving it to NOTHING is a real outcome that has to be reported, not treated as "no
        // filter". A status nobody uses must return no rows, never every row.
        long[]? statusIds = null;
        if (status is not null)
        {
            var lowered = status.ToLowerInvariant();
            statusIds = await _db.SetupMasters.AsNoTracking()
                .Where(s => s.BusinessUnitId == bu
                            && (s.SetupCode != null && s.SetupCode.ToLower() == lowered
                                || s.SetupValue.ToLower() == lowered))
                .Select(s => s.SetupId)
                .ToArrayAsync(cancellationToken);

            if (statusIds.Length == 0)
                notes.Add($"No status called '{status}' is configured in this tenant, so the status " +
                          "filter matched nothing. It was applied, not ignored.");
        }

        foreach (var entity in requested)
        {
            if (!await MayReadAsync(entity, request.RoleId, bu))
            {
                denied.Add(entity);
                continue;
            }

            searched.Add(entity);

            // One MORE row than we intend to show, so "there are more" is a fact rather than an
            // inference. Taking exactly `limit` and then reporting truncation whenever the page is
            // full would claim more results exist every time the count landed exactly on the limit
            // — a small false statement of precisely the kind this endpoint replaces.
            var probe = limit + 1;
            var found = entity switch
            {
                SearchEntities.Customer => await SearchCustomersAsync(request, pattern, status, probe, notes, cancellationToken),
                SearchEntities.Supplier => await SearchSuppliersAsync(request, pattern, status, probe, notes, cancellationToken),
                SearchEntities.Product => await SearchProductsAsync(request, pattern, status, probe, notes, cancellationToken),
                SearchEntities.Lead => await SearchLeadsAsync(request, pattern, statusIds, probe, cancellationToken),
                SearchEntities.Rfq => await SearchRfqsAsync(request, pattern, status, probe, notes, cancellationToken),
                SearchEntities.Quote => await SearchQuotesAsync(request, pattern, statusIds, probe, cancellationToken),
                SearchEntities.Order => await SearchOrdersAsync(request, pattern, statusIds, probe, cancellationToken),
                SearchEntities.Shipment => await SearchShipmentsAsync(request, pattern, statusIds, probe, cancellationToken),
                _ => []
            };

            if (found.Count > limit)
            {
                truncated.Add(entity);
                found.RemoveRange(limit, found.Count - limit);
            }
            hits.AddRange(found);
        }

        return new GlobalSearchResponse(term, hits, searched, denied, truncated, notes);
    }

    /// <summary>
    /// The same rule <see cref="PermissionHandler"/> applies to a controller action, applied here
    /// per family. Restated rather than reused because the handler answers an MVC authorization
    /// context, not a question; the RULE — rank at or above Admin, otherwise an explicit row — is
    /// deliberately identical, and a change to one without the other is a divergence worth catching
    /// in review.
    /// </summary>
    private async Task<bool> MayReadAsync(string entity, long roleId, long businessUnitId)
    {
        if (roleId <= 0) return false;
        if (await _roleGate.GetRoleRankAsync(roleId, businessUnitId) >= RoleRanks.Admin) return true;
        return await _permissions.CheckPermissionAsync(
            roleId, SearchEntities.ModuleFor(entity), nameof(PermissionAction.View), businessUnitId);
    }

    // ── the families ─────────────────────────────────────────────────────────

    private async Task<List<SearchHit>> SearchCustomersAsync(
        GlobalSearchRequest request, string pattern, string? status, int limit,
        List<string> notes, CancellationToken ct)
    {
        if (status is not null)
        {
            notes.Add("Customers carry no status column, only an active flag, so the status filter " +
                      "excluded them from this search rather than matching them all.");
            return [];
        }

        // FR-CST-02: the SAME predicate the customer list uses. Quick search is not a side door.
        var rows = await Dated(_db.Customers.AsNoTracking()
                .Where(c => c.Buid == request.BusinessUnitId)
                .InAccountScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
                .Where(c => c.Name.ToLower().Contains(pattern)
                            || c.DocId != null && c.DocId.ToLower().Contains(pattern)
                            || c.ContactEmail != null && c.ContactEmail.ToLower().Contains(pattern)
                            || c.CommercialRegistrationNumber != null
                               && c.CommercialRegistrationNumber.ToLower().Contains(pattern)
                            || c.TaxRegistrationNumber != null
                               && c.TaxRegistrationNumber.ToLower().Contains(pattern)),
                request, c => c.CreatedOn)
            .OrderByDescending(c => c.CreatedOn)
            .Take(limit)
            .Select(c => new
            {
                c.Id, c.Name, c.DocId, c.ContactEmail, c.CreatedOn,
                c.CommercialRegistrationNumber, c.TaxRegistrationNumber,
                AccountTeamName = c.AccountTeam != null ? c.AccountTeam.TeamName : null
            })
            .ToListAsync(ct);

        return rows.Select(c => new SearchHit(
            SearchEntities.Customer, c.Id, c.Name,
            // The account team is shown on the hit, so a rep can see whose book a customer is in
            // straight from the search — and so an unassigned account is visibly unassigned rather
            // than looking like every other row.
            c.AccountTeamName is { Length: > 0 } team ? $"{c.DocId} · {team}" : $"{c.DocId} · No account team",
            null, c.CreatedOn, "Customers.CreatedOn",
            MatchedField(pattern,
                ("name", c.Name), ("reference", c.DocId), ("email", c.ContactEmail),
                ("CR number", c.CommercialRegistrationNumber),
                ("VAT number", c.TaxRegistrationNumber))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchSuppliersAsync(
        GlobalSearchRequest request, string pattern, string? status, int limit,
        List<string> notes, CancellationToken ct)
    {
        if (status is not null)
        {
            notes.Add("Suppliers carry governance statuses rather than a document status, which the " +
                      "quick-search status filter does not span, so they were excluded from this search.");
            return [];
        }

        var rows = await Dated(_db.Suppliers.AsNoTracking()
                .Where(s => s.Buid == request.BusinessUnitId)
                .Where(s => s.Name.ToLower().Contains(pattern)
                            || s.DocId != null && s.DocId.ToLower().Contains(pattern)
                            || s.ContactEmail != null && s.ContactEmail.ToLower().Contains(pattern)
                            || s.TaxRegistrationNumber != null
                               && s.TaxRegistrationNumber.ToLower().Contains(pattern)),
                request, s => s.CreatedOn)
            .OrderByDescending(s => s.CreatedOn)
            .Take(limit)
            .Select(s => new { s.Id, s.Name, s.DocId, s.ContactEmail, s.CreatedOn, s.TaxRegistrationNumber, s.GovernanceStatus })
            .ToListAsync(ct);

        return rows.Select(s => new SearchHit(
            SearchEntities.Supplier, s.Id, s.Name, s.DocId, s.GovernanceStatus,
            s.CreatedOn, "Suppliers.CreatedOn",
            MatchedField(pattern, ("name", s.Name), ("reference", s.DocId),
                ("email", s.ContactEmail), ("VAT number", s.TaxRegistrationNumber))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchProductsAsync(
        GlobalSearchRequest request, string pattern, string? status, int limit,
        List<string> notes, CancellationToken ct)
    {
        if (status is not null)
        {
            notes.Add("Products carry no document status, so the status filter excluded them from " +
                      "this search rather than matching them all.");
            return [];
        }

        var rows = await Dated(_db.Products.AsNoTracking()
                .Where(p => p.Buid == request.BusinessUnitId)
                .Where(p => p.PartNo.ToLower().Contains(pattern)
                            || p.ProductName != null && p.ProductName.ToLower().Contains(pattern)
                            || p.ModelNo != null && p.ModelNo.ToLower().Contains(pattern)
                            || p.DocId != null && p.DocId.ToLower().Contains(pattern)
                            || p.Barcode != null && p.Barcode.ToLower().Contains(pattern)),
                request, p => p.CreatedOn)
            .OrderByDescending(p => p.CreatedOn)
            .Take(limit)
            .Select(p => new { p.Id, p.PartNo, p.ProductName, p.ModelNo, p.DocId, p.Barcode, p.CreatedOn })
            .ToListAsync(ct);

        return rows.Select(p => new SearchHit(
            SearchEntities.Product, p.Id, p.ProductName ?? p.PartNo, p.PartNo, null,
            p.CreatedOn, "Products.CreatedOn",
            MatchedField(pattern, ("part number", p.PartNo), ("name", p.ProductName),
                ("model", p.ModelNo), ("reference", p.DocId), ("barcode", p.Barcode))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchLeadsAsync(
        GlobalSearchRequest request, string pattern, long[]? statusIds, int limit, CancellationToken ct)
    {
        var query = _db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == request.BusinessUnitId)
            .InCommercialScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
            .Where(l => l.Rfqno != null && l.Rfqno.ToLower().Contains(pattern)
                        || l.BuyersName != null && l.BuyersName.ToLower().Contains(pattern)
                        || l.Clientemail != null && l.Clientemail.ToLower().Contains(pattern)
                        || l.OpportunityNo != null && l.OpportunityNo.ToLower().Contains(pattern)
                        || l.CommercialCaseReference != null
                           && l.CommercialCaseReference.ToLower().Contains(pattern));

        if (statusIds is not null)
            query = query.Where(l => l.LeadStatusId != null && statusIds.Contains(l.LeadStatusId.Value));

        var rows = await Dated(query, request, l => l.CreatedDate)
            .OrderByDescending(l => l.CreatedDate)
            .Take(limit)
            .Select(l => new
            {
                l.Id, l.Rfqno, l.BuyersName, l.Clientemail, l.OpportunityNo,
                l.CommercialCaseReference, l.CreatedDate,
                Status = l.LeadStatus != null ? l.LeadStatus.SetupValue : null
            })
            .ToListAsync(ct);

        return rows.Select(l => new SearchHit(
            SearchEntities.Lead, l.Id, l.Rfqno ?? l.CommercialCaseReference ?? $"Lead {l.Id}",
            l.BuyersName, l.Status, l.CreatedDate, "Leads.CreatedDate",
            MatchedField(pattern, ("enquiry number", l.Rfqno), ("buyer", l.BuyersName),
                ("email", l.Clientemail), ("opportunity", l.OpportunityNo),
                ("case reference", l.CommercialCaseReference))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchRfqsAsync(
        GlobalSearchRequest request, string pattern, string? status, int limit,
        List<string> notes, CancellationToken ct)
    {
        if (status is not null)
        {
            notes.Add("RFQs carry a bidding decision rather than a status row, which the " +
                      "quick-search status filter does not span, so they were excluded from this search.");
            return [];
        }

        var rows = await Dated(_db.Rfqs.AsNoTracking()
                .Where(r => r.BusinessUnitId == request.BusinessUnitId)
                .InCommercialScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
                .Where(r => r.Rfqno.ToLower().Contains(pattern)
                            || r.BuyersName != null && r.BuyersName.ToLower().Contains(pattern)
                            || r.OpportunityNo != null && r.OpportunityNo.ToLower().Contains(pattern)),
                request, r => r.RecDate)
            .OrderByDescending(r => r.RecDate)
            .Take(limit)
            .Select(r => new { r.Id, r.Rfqno, r.BuyersName, r.OpportunityNo, r.RecDate, r.BiddingDecision })
            .ToListAsync(ct);

        return rows.Select(r => new SearchHit(
            SearchEntities.Rfq, r.Id, r.Rfqno, r.BuyersName, r.BiddingDecision,
            r.RecDate, "RFQ.RecDate",
            MatchedField(pattern, ("RFQ number", r.Rfqno), ("buyer", r.BuyersName),
                ("opportunity", r.OpportunityNo))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchQuotesAsync(
        GlobalSearchRequest request, string pattern, long[]? statusIds, int limit, CancellationToken ct)
    {
        var query = _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == request.BusinessUnitId)
            .InCommercialScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
            .Where(q => q.QuoteNo.ToLower().Contains(pattern)
                        || q.Customer != null && q.Customer.Name.ToLower().Contains(pattern));

        if (statusIds is not null)
            query = query.Where(q => q.StatusId != null && statusIds.Contains(q.StatusId.Value));

        var rows = await Dated(query, request, q => q.QuoteDate)
            .OrderByDescending(q => q.QuoteDate)
            .Take(limit)
            .Select(q => new
            {
                q.Id, q.QuoteNo, q.QuoteDate,
                CustomerName = q.Customer != null ? q.Customer.Name : null,
                Status = q.Status != null ? q.Status.SetupValue : null
            })
            .ToListAsync(ct);

        return rows.Select(q => new SearchHit(
            SearchEntities.Quote, q.Id, q.QuoteNo, q.CustomerName, q.Status,
            q.QuoteDate, "Quotes.QuoteDate",
            MatchedField(pattern, ("quote number", q.QuoteNo), ("customer", q.CustomerName))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchOrdersAsync(
        GlobalSearchRequest request, string pattern, long[]? statusIds, int limit, CancellationToken ct)
    {
        var query = _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == request.BusinessUnitId)
            .InCommercialScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
            .Where(o => o.OrderNo.ToLower().Contains(pattern)
                        || o.Customer != null && o.Customer.Name.ToLower().Contains(pattern));

        if (statusIds is not null)
            query = query.Where(o => statusIds.Contains(o.StatusId));

        var rows = await Dated(query, request, o => o.OrderDate)
            .OrderByDescending(o => o.OrderDate)
            .Take(limit)
            .Select(o => new
            {
                o.Id, o.OrderNo, o.OrderDate,
                CustomerName = o.Customer != null ? o.Customer.Name : null,
                Status = o.Status != null ? o.Status.SetupValue : null
            })
            .ToListAsync(ct);

        return rows.Select(o => new SearchHit(
            SearchEntities.Order, o.Id, o.OrderNo, o.CustomerName, o.Status,
            o.OrderDate, "Orders.OrderDate",
            MatchedField(pattern, ("order number", o.OrderNo), ("customer", o.CustomerName))))
            .ToList();
    }

    private async Task<List<SearchHit>> SearchShipmentsAsync(
        GlobalSearchRequest request, string pattern, long[]? statusIds, int limit, CancellationToken ct)
    {
        var orderIds = _db.Orders.AsNoTracking()
            .Where(o => o.BusinessUnitId == request.BusinessUnitId)
            .InCommercialScope(_db, request.BusinessUnitId, request.Scope, DateTime.UtcNow)
            .Select(o => o.Id);
        var query = _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == request.BusinessUnitId)
            .Where(s => orderIds.Contains(s.OrderId))
            .Where(s => s.ShipmentNo.ToLower().Contains(pattern)
                        || s.TrackingNumber != null && s.TrackingNumber.ToLower().Contains(pattern)
                        || s.Carrier != null && s.Carrier.ToLower().Contains(pattern));

        if (statusIds is not null)
            query = query.Where(s => statusIds.Contains(s.StatusId));

        var rows = await Dated(query, request, s => s.ShipmentDate)
            .OrderByDescending(s => s.ShipmentDate)
            .Take(limit)
            .Select(s => new
            {
                s.Id, s.ShipmentNo, s.TrackingNumber, s.Carrier, s.ShipmentDate,
                Status = s.Status != null ? s.Status.SetupValue : null
            })
            .ToListAsync(ct);

        return rows.Select(s => new SearchHit(
            SearchEntities.Shipment, s.Id, s.ShipmentNo,
            s.TrackingNumber ?? s.Carrier, s.Status, s.ShipmentDate, "Shipments.ShipmentDate",
            MatchedField(pattern, ("shipment number", s.ShipmentNo),
                ("tracking number", s.TrackingNumber), ("carrier", s.Carrier))))
            .ToList();
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies the date filter to whichever column that family actually dates itself by.
    ///
    /// <para>Half-open <c>[from, to)</c>, the same convention as every other window in the codebase.
    /// A row whose date column is NULL is EXCLUDED when a filter is present — it is not silently
    /// kept, because "no date recorded" cannot be said to fall inside a range, and it is not
    /// silently coalesced to today, which would put undated records inside every recent window.</para>
    /// </summary>
    private static IQueryable<T> Dated<T>(
        IQueryable<T> source,
        GlobalSearchRequest request,
        System.Linq.Expressions.Expression<Func<T, DateTime?>> date)
    {
        if (request.From is null && request.To is null) return source;

        var parameter = date.Parameters[0];
        System.Linq.Expressions.Expression body =
            System.Linq.Expressions.Expression.NotEqual(
                date.Body, System.Linq.Expressions.Expression.Constant(null, typeof(DateTime?)));

        if (request.From is DateTime from)
            body = System.Linq.Expressions.Expression.AndAlso(body,
                System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    date.Body, System.Linq.Expressions.Expression.Constant(from, typeof(DateTime?))));

        if (request.To is DateTime to)
            body = System.Linq.Expressions.Expression.AndAlso(body,
                System.Linq.Expressions.Expression.LessThan(
                    date.Body, System.Linq.Expressions.Expression.Constant(to, typeof(DateTime?))));

        return source.Where(
            System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter));
    }

    private static IQueryable<T> Dated<T>(
        IQueryable<T> source,
        GlobalSearchRequest request,
        System.Linq.Expressions.Expression<Func<T, DateTime>> date)
    {
        var lifted = System.Linq.Expressions.Expression.Lambda<Func<T, DateTime?>>(
            System.Linq.Expressions.Expression.Convert(date.Body, typeof(DateTime?)),
            date.Parameters);
        return Dated(source, request, lifted);
    }

    /// <summary>
    /// Which field the term hit. Evaluated in memory over the already-selected row, so it costs no
    /// extra query, and it is reported rather than assumed: a hit on a VAT number looks like a
    /// mistake next to a name search until the row says which column matched.
    /// </summary>
    private static string MatchedField(string pattern, params (string Label, string? Value)[] fields)
    {
        foreach (var (label, value) in fields)
            if (value is { Length: > 0 } &&
                value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return label;
        return "related record";
    }
}
