using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Procurement.Discovery;

/// <summary>
/// "Ask suppliers" when the company knows too few: searches the internet for companies that supply
/// the case's part and maker, ranks them the way a buyer trusts them, and — only when the rep ticks
/// them — puts them on the company's supplier list so the existing candidate rule and outreach flow
/// take over. Nothing is created and nothing is sent without the tick.
///
/// <para>Gates, in order: a search key must exist (an administrator's job), the tenant must have
/// switched internet search on under AI governance (the same allow-list the AI provider uses, for
/// the same host), the cache is consulted, and only then does a query leave the box. A provider
/// failure is one plain sentence to the rep and a full entry in the log.</para>
///
/// <para>Metering: the AI token ledger reserves and settles tokens per model call; a web search
/// has no tokens, no model and no prompt, so it does not fit that ledger. Each live search is
/// instead recorded as a structured information line ("SupplierDiscovery searched the internet"),
/// and as a row in <c>supplier_discovery_searches</c>, which is the count of paid calls.</para>
/// </summary>
public sealed class SupplierDiscoveryService : ISupplierDiscoveryService
{
    // Tenant-facing. Never names the provider or the setting: the search engine behind this is
    // Nexora's raw material, configured once at platform level, and clients only see the finished
    // result (owner decision, 2026-09-16). The platform-side detail goes to the log below.
    public const string NotConfiguredMessage =
        "Internet supplier search is not available right now. Nexora is finishing the setup; add the supplier yourself in the meantime.";

    public const string ErrorMessage =
        "The internet search did not answer. Try again in a minute, or add the supplier yourself.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ErpRfqAutomationContext _db;
    private readonly ISupplierWebSearchProvider _provider;
    private readonly IProcurementApplicationService _procurement;
    private readonly IConfiguration _configuration;
    private readonly ISupplierContactFinder _contacts;
    private readonly ILogger<SupplierDiscoveryService> _log;

    /// <summary>How many contact lookups one page may spend, and how many run at once.</summary>
    public const int MaxContactLookupsPerPage = 10;
    private const int ContactLookupConcurrency = 4;
    private static readonly TimeSpan ContactLookupTimeout = TimeSpan.FromSeconds(12);

    public SupplierDiscoveryService(
        ErpRfqAutomationContext db,
        ISupplierWebSearchProvider provider,
        IProcurementApplicationService procurement,
        IConfiguration configuration,
        ISupplierContactFinder contacts,
        ILogger<SupplierDiscoveryService> log)
    {
        _db = db;
        _provider = provider;
        _procurement = procurement;
        _configuration = configuration;
        _contacts = contacts;
        _log = log;
    }

    public async Task<SupplierDiscoveryResult> DiscoverAsync(DiscoverSuppliersCommand command, CancellationToken ct = default)
    {
        EnsureTenant(command.BusinessUnitId);
        SupplierDiscoveryRanker.ValidatePage(command.Offset, command.Limit);
        var sourcingCase = await LoadCaseAsync(command.BusinessUnitId, command.SourcingCaseId, ct);
        var identity = await IdentityForAsync(sourcingCase, ct);
        return await RunAsync(command.BusinessUnitId, identity, command.Offset, command.Limit,
            command.Actor, sourcingCase.Id, ct);
    }

    public async Task<SupplierDiscoveryResult> SearchAsync(
        long businessUnitId, string query, int offset, int limit, CancellationToken ct = default)
    {
        EnsureTenant(businessUnitId);
        SupplierDiscoveryRanker.ValidatePage(offset, limit);
        if (string.IsNullOrWhiteSpace(query))
            throw new ProcurementValidationException("Type what you are looking for — a part number, a maker, or both.");
        var identity = SupplierDiscoveryIdentity.FromQuery(query);
        return await RunAsync(businessUnitId, identity, offset, limit, "supplier-page", sourcingCaseId: null, ct);
    }

    public async Task<AdoptDiscoveredSuppliersResult> AdoptAsync(AdoptDiscoveredSuppliersCommand command, CancellationToken ct = default)
    {
        EnsureTenant(command.BusinessUnitId);
        if (string.IsNullOrWhiteSpace(command.Actor))
            throw new ProcurementValidationException("An authenticated actor is required.");
        var hitIds = (command.HitIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hitIds.Length == 0)
            throw new ProcurementValidationException("Tick at least one supplier to add.");

        var sourcingCase = await LoadCaseAsync(command.BusinessUnitId, command.SourcingCaseId, ct);
        var identity = await IdentityForAsync(sourcingCase, ct);
        var cached = await _db.SupplierDiscoverySearches
            .SingleOrDefaultAsync(x => x.BusinessUnitId == command.BusinessUnitId && x.IdentityKey == identity.Key(), ct)
            ?? throw new ProcurementConflictException("The internet search results have expired. Search again, then tick the suppliers to add.");
        var storedHits = Deserialize(cached.HitsJson).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var missing = hitIds.Where(id => !storedHits.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
            throw new ProcurementConflictException("One of the ticked suppliers is no longer in the search results. Search again and tick it afresh.");

        var now = DateTime.UtcNow;
        var actor = command.Actor.Trim();
        var tenantSuppliers = await _db.Suppliers
            .Where(x => x.Buid == command.BusinessUnitId)
            .Select(x => new { x.Id, x.Name, x.Website, x.ContactEmail })
            .ToListAsync(ct);
        var byDomain = new Dictionary<string, (long Id, string Name, string? ContactEmail)>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplier in tenantSuppliers.OrderBy(x => x.Id))
            if (SupplierDiscoveryClassifier.RegistrableDomain(supplier.Website) is { } domain)
                byDomain.TryAdd(domain, (supplier.Id, supplier.Name, supplier.ContactEmail));
        var emailsInUse = tenantSuppliers.Where(x => x.ContactEmail is not null)
            .Select(x => x.ContactEmail!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var adopted = new List<AdoptedDiscoveredSupplier>();
        var created = new List<(string HitId, Supplier Supplier)>();
        var lookedUpDuringAdopt = false;
        foreach (var hitId in hitIds)
        {
            var hit = storedHits[hitId];
            if (byDomain.TryGetValue(hit.Domain, out var existing))
            {
                adopted.Add(new AdoptedDiscoveredSupplier(hitId, existing.Id, existing.Name, existing.ContactEmail,
                    string.IsNullOrWhiteSpace(existing.ContactEmail), AlreadyExisted: true));
                continue;
            }

            // A ticked company with no address yet gets one more look before it joins the list.
            if (hit.ContactEmail is null && !hit.ContactLookedUp)
            {
                var found = await _contacts.FindEmailAsync(hit.Domain, hit.Name, ct);
                hit = hit with { ContactEmail = found, ContactLookedUp = true };
                storedHits[hitId] = hit;
                lookedUpDuringAdopt = true;
            }

            // Two suppliers cannot share one dispatch address (UX_Suppliers_BU_ContactEmail), so an
            // address already on another supplier is left for the rep to sort out by hand.
            var contactEmail = hit.ContactEmail is { } email && !emailsInUse.Contains(email) ? email : null;
            if (contactEmail is not null) emailsInUse.Add(contactEmail);

            var supplier = new Supplier
            {
                Name = hit.Name,
                Website = hit.Website,
                Role = hit.Role,
                ContactEmail = contactEmail,
                ImageUrl = string.Empty,
                Tags = identity.Tags(),
                Comments = $"Found on the internet for {identity.Subject()} on {now:yyyy-MM-dd}: {hit.Why}.",
                GovernanceStatus = SupplierGovernanceStatuses.Discovered,
                Tier = SupplierTiers.Tier3OutOfNetwork,
                Buid = command.BusinessUnitId,
                IsActive = true,
                ConcurrencyToken = Guid.NewGuid(),
                CreatedBy = actor,
                CreatedOn = now
            };
            _db.Suppliers.Add(supplier);
            created.Add((hitId, supplier));
        }

        if (lookedUpDuringAdopt)
            cached.HitsJson = JsonSerializer.Serialize(storedHits.Values.OrderBy(x => x.ProviderOrder).ToList(), Json);
        if (created.Count > 0 || lookedUpDuringAdopt)
            await _db.SaveChangesAsync(ct);
        foreach (var (hitId, supplier) in created)
            adopted.Add(new AdoptedDiscoveredSupplier(hitId, supplier.Id, supplier.Name, supplier.ContactEmail,
                string.IsNullOrWhiteSpace(supplier.ContactEmail), AlreadyExisted: false));

        _log.LogInformation(
            "SupplierDiscovery adopted {Created} new and {Existing} existing supplier(s) for tenant {Tenant} on sourcing case {Case}.",
            created.Count, adopted.Count - created.Count, command.BusinessUnitId, sourcingCase.Id);

        // The same candidate rule the "search known suppliers" button runs, so the new suppliers
        // appear as candidates with their blockers spelled out (no contact email, not yet approved).
        await _procurement.RefreshCandidatesAfterSupplierChangeAsync(new RefreshSourcingCandidatesCommand(
            command.BusinessUnitId, sourcingCase.Id, actor, command.CorrelationId), ct);

        return new AdoptDiscoveredSuppliersResult(adopted.OrderBy(x => Array.IndexOf(hitIds, x.HitId)).ToArray());
    }

    // ---- the search itself -------------------------------------------------

    private async Task<SupplierDiscoveryResult> RunAsync(
        long businessUnitId, SupplierDiscoveryIdentity identity, int offset, int limit, string actor,
        long? sourcingCaseId, CancellationToken ct)
    {
        var searchedFor = identity.SearchedFor();
        SupplierDiscoveryResult Empty(string status, string message) =>
            new(status, message, searchedFor, 0, offset, limit, false, null, []);

        if (SupplierDiscoveryConfiguration.ApiKey(_configuration) is null)
        {
            _log.LogWarning(
                "Supplier discovery is not configured at platform level: set SupplierDiscovery:ApiKey (or Ollama:ApiKey). Business unit {BusinessUnitId} saw the neutral not-available message.",
                businessUnitId);
            return Empty(SupplierDiscoveryStatuses.NotConfigured, NotConfiguredMessage);
        }

        // No consent step. A company that bought Nexora bought this: external suppliers and AI
        // features are part of the terms, so the only thing that can stop a search is a
        // missing search key (owner decision, 2026-09-16).

        if (identity.IsEmpty)
            return Empty(SupplierDiscoveryStatuses.NoResults,
                "This line has no part number, maker or description to search for. Try adding a supplier yourself.");

        var now = DateTime.UtcNow;
        var key = identity.Key();
        var cached = await _db.SupplierDiscoverySearches
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdentityKey == key, ct);

        IReadOnlyList<SupplierDiscoveryStoredHit> hits;
        bool fromCache;
        DateTime searchedAt;
        if (cached is not null && cached.IsFresh(now))
        {
            hits = Deserialize(cached.HitsJson);
            fromCache = true;
            searchedAt = cached.SearchedAtUtc;
        }
        else
        {
            var queries = identity.Queries();
            List<SupplierDiscoveryStoredHit> live;
            try
            {
                live = await SearchLiveAsync(identity, queries, ct);
            }
            catch (SupplierWebSearchException exception)
            {
                _log.LogError(exception,
                    "SupplierDiscovery provider failure for tenant {Tenant}, case {Case}, subject {Subject}, {QueryCount} query(ies).",
                    businessUnitId, sourcingCaseId, identity.Subject(), queries.Count);
                return Empty(SupplierDiscoveryStatuses.Error, ErrorMessage);
            }

            _log.LogInformation(
                "SupplierDiscovery searched the internet: tenant {Tenant}, case {Case}, subject {Subject}, provider {Provider} at {Endpoint}, {QueryCount} query(ies), {HitCount} hit(s) after filtering.",
                businessUnitId, sourcingCaseId, identity.Subject(), _provider.Destination.Provider,
                _provider.Destination.Endpoint, queries.Count, live.Count);

            hits = live;
            fromCache = false;
            searchedAt = now;
            if (live.Count > 0)
            {
                // An empty answer is not cached: the rep may try again once the internet has caught up.
                if (cached is null)
                {
                    cached = new SupplierDiscoverySearch { BusinessUnitId = businessUnitId, IdentityKey = key };
                    _db.SupplierDiscoverySearches.Add(cached);
                }
                cached.Subject = Truncate(identity.Subject(), 255);
                cached.Provider = _provider.Destination.Provider;
                cached.QueriesJson = JsonSerializer.Serialize(queries, Json);
                cached.HitsJson = JsonSerializer.Serialize(live, Json);
                cached.HitCount = live.Count;
                cached.SearchedAtUtc = now;
                cached.SearchedBy = Truncate(actor, 255);
                await _db.SaveChangesAsync(ct);
            }
        }

        if (hits.Count == 0)
            return Empty(SupplierDiscoveryStatuses.NoResults,
                $"Nothing found on the internet for {identity.Subject()}. Try adding a supplier yourself.");

        var known = await _db.Suppliers.AsNoTracking()
            .Where(x => x.Buid == businessUnitId)
            .Select(x => new KnownSupplier(x.Id, x.Name, x.Website, x.ContactEmail))
            .ToListAsync(ct);
        var ranked = SupplierDiscoveryRanker.Rank(hits, known);
        var page = SupplierDiscoveryRanker.Page(ranked, offset, limit);

        // A company the rep can see but cannot ask is half an answer. For the hits on this page
        // that arrived without an address, ask the internet for one now, remember the answer in
        // the cache, and rank again so the rows carry it.
        var wanting = page.Where(x => x.ContactEmail is null).Select(x => x.Id).ToArray();
        if (wanting.Length > 0)
        {
            var enriched = await LookUpContactsAsync(cached, hits, wanting, ct);
            if (enriched is not null)
            {
                hits = enriched;
                // A company the rep already added but could not ask gets the address on its record
                // now, unless another supplier already uses it (one dispatch address per supplier).
                var knownWithoutEmail = page.Where(x => x.ExistingSupplierId is not null && x.ContactEmail is null)
                    .Select(x => (SupplierId: x.ExistingSupplierId!.Value, Found: hits.FirstOrDefault(h => h.Id == x.Id)?.ContactEmail))
                    .Where(x => x.Found is not null).ToArray();
                if (knownWithoutEmail.Length > 0)
                {
                    var inUse = known.Where(x => x.ContactEmail is not null).Select(x => x.ContactEmail!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var ids = knownWithoutEmail.Select(x => x.SupplierId).ToArray();
                    var records = await _db.Suppliers.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
                    foreach (var (supplierId, found) in knownWithoutEmail)
                    {
                        var record = records.FirstOrDefault(x => x.Id == supplierId);
                        if (record is null || record.ContactEmail is not null || inUse.Contains(found!)) continue;
                        record.ContactEmail = found;
                        inUse.Add(found!);
                    }
                    await _db.SaveChangesAsync(ct);
                    known = known.Select(k => records.FirstOrDefault(r => r.Id == k.Id) is { ContactEmail: not null } r
                        ? k with { ContactEmail = r.ContactEmail } : k).ToList();
                }
                ranked = SupplierDiscoveryRanker.Rank(hits, known);
                page = SupplierDiscoveryRanker.Page(ranked, offset, limit);
            }
        }
        var knownCount = ranked.Count(x => x.ExistingSupplierId.HasValue);
        var message = fromCache
            ? $"Found {ranked.Count} {Plural(ranked.Count, "company", "companies")} on the internet for {identity.Subject()} (searched {searchedAt:d MMM}). "
            : $"Found {ranked.Count} {Plural(ranked.Count, "company", "companies")} on the internet for {identity.Subject()}. ";
        message += knownCount > 0
            ? $"{knownCount} {Plural(knownCount, "is", "are")} already on your supplier list. Tick the others you want to add, then ask them."
            : "Tick the ones you want to add, then ask them.";

        return new SupplierDiscoveryResult(SupplierDiscoveryStatuses.Ready, message, searchedFor,
            ranked.Count, offset, limit, fromCache, searchedAt, page);
    }

    /// <summary>
    /// Looks up an enquiry address for the given hits, a few at a time and never more than
    /// <see cref="MaxContactLookupsPerPage"/> per call, and writes the answers back to the cache.
    /// Returns null when nothing changed.
    /// </summary>
    private async Task<IReadOnlyList<SupplierDiscoveryStoredHit>?> LookUpContactsAsync(
        SupplierDiscoverySearch? cached, IReadOnlyList<SupplierDiscoveryStoredHit> hits, IReadOnlyList<string> hitIds, CancellationToken ct)
    {
        var byId = hits.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var todo = hitIds.Where(id => byId.TryGetValue(id, out var hit) && hit.ContactEmail is null && !hit.ContactLookedUp)
            .Take(MaxContactLookupsPerPage).ToArray();
        if (todo.Length == 0) return null;

        using var gate = new SemaphoreSlim(ContactLookupConcurrency);
        var results = await Task.WhenAll(todo.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ContactLookupTimeout);
                var hit = byId[id];
                return (id, Email: await _contacts.FindEmailAsync(hit.Domain, hit.Name, timeout.Token));
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (id, email) in results)
            byId[id] = byId[id] with { ContactEmail = email, ContactLookedUp = true };
        var updated = hits.Select(x => byId[x.Id]).ToList();
        _log.LogInformation("SupplierDiscovery contact lookup: {Found} of {Tried} companies now have an address.",
            results.Count(x => x.Email is not null), results.Length);

        if (cached is not null)
        {
            cached.HitsJson = JsonSerializer.Serialize(updated, Json);
            await _db.SaveChangesAsync(ct);
        }
        return updated;
    }

    private async Task<List<SupplierDiscoveryStoredHit>> SearchLiveAsync(
        SupplierDiscoveryIdentity identity, IReadOnlyList<string> queries, CancellationToken ct)
    {
        var byDomain = new Dictionary<string, SupplierDiscoveryStoredHit>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        SupplierWebSearchException? lastFailure = null;
        foreach (var query in queries)
        {
            IReadOnlyList<WebSearchResult> results;
            try
            {
                results = await _provider.SearchAsync(query, SupplierDiscoveryConfiguration.ProviderMaxResultsPerQuery, ct);
            }
            catch (SupplierWebSearchException exception)
            {
                // One query failing after others answered is not a failed search; none answering is.
                lastFailure = exception;
                continue;
            }
            foreach (var result in results)
            {
                var hit = SupplierDiscoveryClassifier.Classify(result, identity.Makers, identity.PartNumbers, order++, identity.ProductWords);
                if (hit is null) continue;
                // First appearance wins: the search engine ranked it higher, and one domain is one company.
                byDomain.TryAdd(hit.Domain, hit);
            }
        }
        if (byDomain.Count == 0 && lastFailure is not null)
            throw lastFailure;
        return byDomain.Values.OrderBy(x => x.ProviderOrder).ToList();
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<SourcingCase> LoadCaseAsync(long businessUnitId, long sourcingCaseId, CancellationToken ct)
        => await _db.SourcingCases.AsNoTracking()
               .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == sourcingCaseId, ct)
           ?? throw new ProcurementValidationException("Sourcing Case was not found in the authenticated tenant.");

    /// <summary>
    /// The case's own part number and maker first; the RFQ line fills in what the case does not
    /// carry, including the customer's approved-maker list — read from the RFQ line's copy, or from
    /// the lead line that produced it for RFQ lines older than that copy.
    /// </summary>
    private async Task<SupplierDiscoveryIdentity> IdentityForAsync(SourcingCase sourcingCase, CancellationToken ct)
    {
        var line = await _db.Rfqitems.AsNoTracking()
            .Where(x => x.Id == sourcingCase.RfqItemId && x.Rfqid == sourcingCase.RfqId)
            .Select(x => new
            {
                x.ManufacturerName, x.ManufacturerPartNumber, x.ProductShortDescription, x.ProductShortName,
                x.ItemText, x.ExtraFields, x.SourceLeadItemRevisionId
            })
            .SingleOrDefaultAsync(ct);

        var approvedMakers = ApprovedMakers(line?.ExtraFields);
        if (approvedMakers is null && line?.SourceLeadItemRevisionId is { } revisionId)
        {
            var leadExtraFields = await _db.Set<LeadIdentity.LeadItemRevision>().AsNoTracking()
                .Where(x => x.Id == revisionId && x.LeadItem != null)
                .Select(x => x.LeadItem!.ExtraFields)
                .FirstOrDefaultAsync(ct);
            approvedMakers = ApprovedMakers(leadExtraFields);
        }

        return SupplierDiscoveryIdentity.From(
            FirstNonBlank(sourcingCase.RequestedPartNumber, line?.ManufacturerPartNumber),
            FirstNonBlank(sourcingCase.Manufacturer, line?.ManufacturerName),
            FirstNonBlank(sourcingCase.Description, line?.ProductShortDescription, line?.ProductShortName, line?.ItemText),
            approvedMakers);
    }

    /// <summary>The customer's "Approved manufacturers" column, read the way the supplier RFQ email reads it.</summary>
    internal static string? ApprovedMakers(string? extraFieldsJson)
    {
        var extra = ExtraFieldsJson.Deserialize(extraFieldsJson);
        if (extra is null) return null;
        foreach (var (key, value) in extra)
            if (string.Equals(key.Trim(), "Approved manufacturers", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return null;
    }

    private static IReadOnlyList<SupplierDiscoveryStoredHit> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SupplierDiscoveryStoredHit>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0)
            throw new ProcurementValidationException("A valid authenticated business unit is required.");
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Plural(int count, string one, string many) => count == 1 ? one : many;
}
