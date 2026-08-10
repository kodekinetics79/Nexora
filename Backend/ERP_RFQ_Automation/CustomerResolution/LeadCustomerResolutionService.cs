using System.Net.Mail;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.CustomerResolution;

public interface ILeadCustomerResolutionService
{
    /// <summary>
    /// Resolves ONE lead's client organisation and persists the outcome (link or ranked
    /// candidates). Never overwrites a human decision. Idempotent: running it twice on
    /// unchanged evidence produces the same row state.
    /// </summary>
    Task<ClientResolutionOutcome> ResolveAsync(long businessUnitId, long leadId, CancellationToken ct = default);

    /// <summary>
    /// Tenant-scoped re-run over leads that are not human-decided — the entry point that
    /// lets the 26 existing UNRESOLVED production leads be resolved without re-upload, and
    /// that makes a wrong rule re-runnable after it is fixed.
    /// </summary>
    Task<CustomerResolutionBackfillResult> BackfillAsync(
        long businessUnitId, int maxLeads = 500, bool includeSuggested = true, CancellationToken ct = default);
}

public sealed record CustomerResolutionBackfillResult(
    int Examined, int AutoMatched, int Suggested, int Ambiguous, int Unresolved, int Failed);

/// <summary>
/// The database adapter around the pure <see cref="CustomerIdentityResolver"/>: builds the
/// evidence set from the lead + its ingest, loads a bounded tenant corpus, applies the
/// outcome to the lead and replaces its candidate rows.
///
/// ORDERING (load bearing): this runs AFTER lead-identity reconciliation and persistence and
/// immediately BEFORE routing. LeadIdentityApplicationService.CustomerScope() keys the dedup
/// corpus on <c>customer:{Id}</c> when Lead.CustomerId is set and on <c>email:</c>/<c>buyer:</c>
/// otherwise; resolving earlier would re-key live occurrences and break duplicate/revision
/// detection.
/// </summary>
public sealed class LeadCustomerResolutionService : ILeadCustomerResolutionService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly CustomerResolutionPolicy _policy;
    private readonly ILogger<LeadCustomerResolutionService>? _log;

    public LeadCustomerResolutionService(
        ErpRfqAutomationContext db,
        CustomerResolutionPolicy? policy = null,
        ILogger<LeadCustomerResolutionService>? log = null)
    {
        _db = db;
        _policy = policy ?? new CustomerResolutionPolicy();
        _log = log;
    }

    public async Task<ClientResolutionOutcome> ResolveAsync(
        long businessUnitId, long leadId, CancellationToken ct = default)
    {
        var lead = await _db.Leads
            .Include(l => l.LeadItems)
            .Include(l => l.EmailIngests)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Id == leadId, ct)
            ?? throw new KeyNotFoundException($"Lead {leadId} was not found in business unit {businessUnitId}.");

        var outcome = await ResolveCoreAsync(businessUnitId, lead, ct);
        await _db.SaveChangesAsync(ct);
        return outcome;
    }

    public async Task<CustomerResolutionBackfillResult> BackfillAsync(
        long businessUnitId, int maxLeads = 500, bool includeSuggested = true, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        maxLeads = Math.Clamp(maxLeads, 1, 5_000);

        var statuses = includeSuggested
            ? new[] { LeadCustomerMatchStatuses.Unresolved, LeadCustomerMatchStatuses.Suggested, LeadCustomerMatchStatuses.Ambiguous }
            : [LeadCustomerMatchStatuses.Unresolved];

        var leadIds = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.CustomerId == null
                        && statuses.Contains(l.CustomerMatchStatus))
            .OrderBy(l => l.Id)
            .Select(l => l.Id)
            .Take(maxLeads)
            .ToListAsync(ct);

        int auto = 0, suggested = 0, ambiguous = 0, unresolved = 0, failed = 0;
        foreach (var id in leadIds)
        {
            try
            {
                var outcome = await ResolveAsync(businessUnitId, id, ct);
                switch (outcome.Status)
                {
                    case LeadCustomerMatchStatuses.AutoMatched:
                    case LeadCustomerMatchStatuses.AutoMatchedContactUnresolved:
                        auto++; break;
                    case LeadCustomerMatchStatuses.Suggested: suggested++; break;
                    case LeadCustomerMatchStatuses.Ambiguous: ambiguous++; break;
                    default: unresolved++; break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _db.ChangeTracker.Clear();
                _log?.LogWarning(ex, "Client resolution failed for lead {LeadId}; the lead is unchanged.", id);
            }
        }

        return new CustomerResolutionBackfillResult(leadIds.Count, auto, suggested, ambiguous, unresolved, failed);
    }

    /// <summary>Resolves and stages the writes; the CALLER owns SaveChanges.</summary>
    internal async Task<ClientResolutionOutcome> ResolveCoreAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // A person already decided. Never overwrite, never re-suggest.
        if (LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus))
            return new ClientResolutionOutcome(
                lead.CustomerMatchStatus, lead.CustomerId, lead.ContactId, 1m,
                CustomerMatchReasonCodes.HumanResolved,
                "A reviewer confirmed this client.", []);

        // A prior machine link (or the legacy VERIFIED_EMAIL backfill) stands until a
        // human changes it: re-running resolution must not churn commercial lineage.
        if (lead.CustomerId.HasValue)
            return new ClientResolutionOutcome(
                lead.CustomerMatchStatus, lead.CustomerId, lead.ContactId,
                lead.CustomerMatchConfidence ?? 1m,
                lead.CustomerMatchReasonCode ?? CustomerMatchReasonCodes.HumanResolved,
                lead.CustomerMatchExplanation ?? "This lead is already linked to a client.", []);

        var evidence = await BuildEvidenceAsync(businessUnitId, lead, ct);
        var corpus = await LoadCorpusAsync(businessUnitId, evidence, ct);
        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, _policy);

        switch (outcome.Status)
        {
            case LeadCustomerMatchStatuses.AutoMatched:
            case LeadCustomerMatchStatuses.AutoMatchedContactUnresolved:
                lead.AutoResolveCommercialIdentity(
                    outcome.CustomerId!.Value, outcome.ContactId,
                    outcome.ReasonCode, outcome.Confidence, outcome.Explanation, now);
                break;
            case LeadCustomerMatchStatuses.Ambiguous:
                lead.SuggestCommercialIdentity(
                    outcome.ReasonCode, outcome.Confidence, outcome.Explanation, ambiguous: true, now);
                break;
            case LeadCustomerMatchStatuses.Suggested:
                lead.SuggestCommercialIdentity(
                    outcome.ReasonCode, outcome.Confidence, outcome.Explanation, ambiguous: false, now);
                break;
            default:
                lead.ClearCommercialIdentity(outcome.ReasonCode, outcome.Explanation, now);
                break;
        }

        await ReplaceCandidatesAsync(businessUnitId, lead.Id, outcome.Candidates, now, ct);
        return outcome;
    }

    // ── evidence ──────────────────────────────────────────────────────────────

    private async Task<LeadClientEvidence> BuildEvidenceAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        // TRAP: EmailService writes TWO different shapes of the same fact —
        // message.From.ToString() ("Ali Nasser <ali@se.com.sa>") into EmailIngests.FromEmail,
        // and message.From.Mailboxes.First().Address ("ali@se.com.sa") into the job sidecar
        // that becomes Lead.Clientemail. A raw string compare between them never matches, so
        // both are RFC-parsed down to the bare address here.
        var ingestFrom = lead.EmailIngests?.FromEmail;
        if (string.IsNullOrWhiteSpace(ingestFrom) && lead.EmailIngestsId.HasValue)
        {
            // SEC-ING-01: the tenant predicate is explicit because IgnoreQueryFilters removed the
            // only other one. This lookup was `x.Id == lead.EmailIngestsId` and NOTHING else — the
            // one call in this file (of ten) with no business-unit clause — and it is reachable
            // from the mailbox poller, which until now ran with a null tenant under the BYPASSRLS
            // pipeline role. EmailIngests carries no tenant column of its own, so the predicate
            // goes through the owning mailbox exactly as the table's RLS policy does.
            ingestFrom = await _db.EmailIngests.AsNoTracking().IgnoreQueryFilters()
                .Where(x => x.Id == lead.EmailIngestsId.Value
                            && x.EmailConfiguration.BusinessUnitId == businessUnitId)
                .Select(x => x.FromEmail)
                .SingleOrDefaultAsync(ct);
        }

        var sender = ParseAddress(ingestFrom) ?? ParseAddress(lead.Clientemail);

        var businessUnitName = await _db.BusinessUnits.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitName)
            .SingleOrDefaultAsync(ct);
        var tenantMailboxes = await _db.EmailConfigurations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.EmailAddress != null)
            .Select(c => c.EmailAddress!)
            .Distinct()
            .Take(50)
            .ToListAsync(ct);

        var selfNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(businessUnitName)) selfNames.Add(businessUnitName!);
        if (!string.IsNullOrWhiteSpace(lead.SupplierNameOnDocument)) selfNames.Add(lead.SupplierNameOnDocument!);

        return new LeadClientEvidence
        {
            BusinessUnitId = businessUnitId,
            LeadId = lead.Id,
            SenderEmail = sender,
            DocumentBuyerEmail = ParseAddress(lead.CustomerBuyerEmailExtracted),
            AccountReferences = lead.LeadItems
                .SelectMany(item => new[] { item.CustomerAccountPortalId, item.CompanyRef })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList(),
            CustomerRegistrationId = lead.CustomerCompanyRegistrationId,
            CustomerCompanyName = lead.CustomerCompanyNameExtracted,
            CustomerPortalName = lead.CustomerPortalNameExtracted,
            SupplierAccountRefOnDocument = lead.SupplierAccountRefOnDocument,
            SupplierNameOnDocument = lead.SupplierNameOnDocument,
            RfqNumber = lead.Rfqno,
            BuyerPersonName = lead.BuyersName,
            TenantSelfNameKeys = selfNames,
            TenantSelfDomains = tenantMailboxes
        };
    }

    /// <summary>
    /// RFC-parses an address out of either shape ("Name &lt;a@b&gt;" or "a@b") and lowercases it.
    /// Returns null for anything that is not a single deliverable address.
    /// </summary>
    internal static string? ParseAddress(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (MailAddress.TryCreate(trimmed, out var parsed) && !string.IsNullOrWhiteSpace(parsed.Address))
            return parsed.Address.Trim().ToLowerInvariant();
        // Tolerate a bare "a@b" that MailAddress rejects for display-name reasons.
        var at = trimmed.LastIndexOf('@');
        return at > 0 && at < trimmed.Length - 1 && !trimmed.Contains(' ')
            ? trimmed.ToLowerInvariant()
            : null;
    }

    // ── corpus ────────────────────────────────────────────────────────────────

    private async Task<ClientResolutionCorpus> LoadCorpusAsync(
        long businessUnitId, LeadClientEvidence evidence, CancellationToken ct)
    {
        var addresses = new[] { evidence.SenderEmail, evidence.DocumentBuyerEmail }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();
        var domains = addresses
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(d => !string.IsNullOrWhiteSpace(d) && !SyntheticIdentityGuard.IsSyntheticDomain(d)
                        && !SyntheticIdentityGuard.IsFreeMailDomain(d))
            .Select(d => d!).Distinct().ToArray();
        var accounts = evidence.AccountReferences
            .Select(x => RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, x))
            .Where(x => x.Length > 0).Distinct().ToArray();
        var taxRegistrations = string.IsNullOrWhiteSpace(evidence.CustomerRegistrationId)
            ? []
            : new[] { RoutingValueNormalizer.Normalize(CustomerIdentifierType.TaxRegistration, evidence.CustomerRegistrationId) };
        var nameKey = CustomerNameNormalizer.LooseKey(evidence.CustomerCompanyName);
        var portalKey = CustomerNameNormalizer.LooseKey(evidence.CustomerPortalName);
        var supplierAccountKey = string.IsNullOrWhiteSpace(evidence.SupplierAccountRefOnDocument)
            ? string.Empty
            : RoutingValueNormalizer.Normalize(CustomerIdentifierType.ErpAccount, evidence.SupplierAccountRefOnDocument);
        var portalAccountKey = portalKey.Length > 0 && supplierAccountKey.Length > 0
            ? $"{portalKey}|{supplierAccountKey}"
            : string.Empty;
        var hasRfq = !string.IsNullOrWhiteSpace(evidence.RfqNumber);

        // Only active customers of this tenant can ever be linked.
        var identifiers = await _db.Set<CustomerIdentifier>().AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId && i.EffectiveTo == null)
            // IgnoreQueryFilters is a query-level flag set above; the subquery inherits it.
            .Where(i => _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false))
            .Where(i =>
                (i.IdentifierType == CustomerIdentifierType.Email && addresses.Contains(i.NormalizedValue)) ||
                (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue)) ||
                (i.IdentifierType == CustomerIdentifierType.ErpAccount && accounts.Contains(i.NormalizedValue)) ||
                (i.IdentifierType == CustomerIdentifierType.TaxRegistration && taxRegistrations.Contains(i.NormalizedValue)) ||
                ((i.IdentifierType == CustomerIdentifierType.Alias || i.IdentifierType == CustomerIdentifierType.CustomerName)
                    && nameKey != "" && i.NormalizedValue == nameKey) ||
                (i.IdentifierType == CustomerIdentifierType.PortalAccount
                    && portalAccountKey != "" && i.NormalizedValue == portalAccountKey) ||
                (i.IdentifierType == CustomerIdentifierType.RfqNumberPattern && hasRfq))
            .Select(i => new CustomerIdentifierSnapshot(
                i.Id, i.CustomerId, i.IdentifierType, i.NormalizedValue, i.IsVerified, i.Confidence, i.Source))
            .Take(500)
            .ToListAsync(ct);

        var customers = await LoadCustomerNamesAsync(businessUnitId, evidence.CustomerCompanyName, identifiers, ct);
        var contacts = await LoadContactsAsync(businessUnitId, addresses, evidence.BuyerPersonName, identifiers, ct);
        var priorSenders = await LoadPriorSenderResolutionsAsync(businessUnitId, evidence.LeadId, addresses, ct);

        return new ClientResolutionCorpus
        {
            Identifiers = identifiers,
            Customers = customers,
            Contacts = contacts,
            PriorSenderResolutions = priorSenders
        };
    }

    private async Task<List<CustomerNameSnapshot>> LoadCustomerNamesAsync(
        long businessUnitId, string? rawName, List<CustomerIdentifierSnapshot> identifiers, CancellationToken ct)
    {
        var matchedIds = identifiers.Select(i => i.CustomerId).Distinct().ToArray();

        // The name scan is read-only and capped. Above the cap we pre-filter in SQL on a
        // leading-character bucket (no pg_trgm, no new Postgres extension). The filter is
        // deliberately WIDER than the comparison it feeds: LooseKey/TightKey cannot run in
        // SQL without a stored normalised column, so SQL narrows on the RAW leading
        // characters and the exact/fuzzy comparison still happens on the normalised keys in
        // C#. Customers already matched by an identifier are always included, so no
        // auto-link tier can ever be degraded by this fallback.
        var query = _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false);

        var total = await query.CountAsync(ct);
        if (total > _policy.MaximumNameScanRows)
        {
            var prefix = new string((rawName ?? string.Empty)
                .Where(char.IsLetterOrDigit).Take(2).Select(char.ToUpperInvariant).ToArray());
            query = prefix.Length == 2
                ? query.Where(c => matchedIds.Contains(c.Id) || c.Name.ToUpper().StartsWith(prefix))
                : query.Where(c => matchedIds.Contains(c.Id));
        }

        return await query
            .OrderBy(c => c.Id)
            .Select(c => new CustomerNameSnapshot(c.Id, c.Name))
            .Take(_policy.MaximumNameScanRows)
            .ToListAsync(ct);
    }

    private async Task<List<CustomerContactSnapshot>> LoadContactsAsync(
        long businessUnitId, string[] addresses, string? buyerPersonName,
        List<CustomerIdentifierSnapshot> identifiers, CancellationToken ct)
    {
        var matchedIds = identifiers.Select(i => i.CustomerId).Distinct().ToArray();
        var surname = CustomerNameNormalizer.LooseKey(buyerPersonName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.Length > 2);

        return await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.CustomerId != null && c.IsActive != false)
            .Where(c => matchedIds.Contains(c.CustomerId!.Value)
                        || (c.Email != null && addresses.Contains(c.Email.ToLower()))
                        || (surname != null && c.LastName.ToUpper() == surname))
            .Select(c => new CustomerContactSnapshot(
                c.Id, c.CustomerId!.Value, c.Email, c.FirstName, c.LastName))
            .Take(200)
            .ToListAsync(ct);
    }

    private async Task<List<PriorSenderResolution>> LoadPriorSenderResolutionsAsync(
        long businessUnitId, long leadId, string[] addresses, CancellationToken ct)
    {
        if (addresses.Length == 0) return [];

        var rows = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId && l.Id != leadId
                        && l.CustomerId != null && l.Clientemail != null
                        && addresses.Contains(l.Clientemail.ToLower()))
            .Select(l => new { Email = l.Clientemail!, CustomerId = l.CustomerId!.Value, l.CustomerMatchStatus })
            .Take(200)
            .ToListAsync(ct);

        // Only a HUMAN-resolved precedent is worth suggesting from; suggesting from an
        // earlier machine guess would let one mistake propagate through the corpus.
        return rows
            .Where(row => LeadCustomerMatchStatuses.IsHumanDecided(row.CustomerMatchStatus))
            .Select(row => new PriorSenderResolution(row.Email.Trim().ToLowerInvariant(), row.CustomerId))
            .Distinct()
            .ToList();
    }

    // ── candidate persistence ─────────────────────────────────────────────────

    private async Task ReplaceCandidatesAsync(
        long businessUnitId, long leadId, IReadOnlyList<ClientMatchCandidate> candidates,
        DateTime now, CancellationToken ct)
    {
        var existing = await _db.Set<LeadCustomerMatchCandidate>().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.LeadId == leadId)
            .OrderBy(c => c.Rank)
            .ToListAsync(ct);

        var wanted = candidates.Take(_policy.MaximumCandidates).ToArray();

        // Rewrite rank slots IN PLACE rather than delete-then-insert: the (tenant, lead,
        // rank) unique index would otherwise depend on EF ordering deletes before inserts
        // inside one SaveChanges, which it does not guarantee.
        for (var index = 0; index < wanted.Length; index++)
        {
            var candidate = wanted[index];
            var row = index < existing.Count ? existing[index] : null;
            if (row is null)
            {
                row = new LeadCustomerMatchCandidate { BusinessUnitId = businessUnitId, LeadId = leadId };
                _db.Set<LeadCustomerMatchCandidate>().Add(row);
            }
            row.CustomerId = candidate.CustomerId;
            row.Rank = index + 1;
            row.Confidence = Math.Clamp(candidate.Confidence, 0m, 1m);
            row.ReasonCode = candidate.ReasonCode;
            row.Explanation = Truncate(candidate.Explanation, 500);
            row.CreatedOn = now;
        }

        if (existing.Count > wanted.Length)
            _db.Set<LeadCustomerMatchCandidate>().RemoveRange(existing.Skip(wanted.Length));
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value[..max]);
}
