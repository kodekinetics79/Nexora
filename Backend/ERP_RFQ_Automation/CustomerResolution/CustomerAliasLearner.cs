using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.CustomerResolution;

public interface ICustomerAliasLearner
{
    /// <summary>
    /// Turns ONE human correction into durable identity knowledge. Stages writes on the
    /// caller's DbContext inside the caller's transaction — the caller owns SaveChanges, so
    /// the learned edges commit with the review that taught them or not at all.
    /// </summary>
    Task<CustomerAliasLearningResult> LearnFromReviewAsync(
        long businessUnitId,
        Lead lead,
        long customerId,
        long? previousCustomerId,
        long? reviewAuditId,
        CancellationToken ct = default);
}

public sealed record CustomerAliasLearningResult(
    int Learned, int Reinforced, int Expired, IReadOnlyList<string> SkipReasons)
{
    public static readonly CustomerAliasLearningResult None = new(0, 0, 0, []);
    public string? SkippedReason => SkipReasons.Count == 0 ? null : string.Join(",", SkipReasons.Distinct());
}

/// <summary>
/// The learning loop: a reviewer picking the right client teaches Nexora to get the NEXT
/// document from that client right by itself.
///
/// It writes into the EXISTING <c>customer_identifiers</c> store rather than a parallel
/// alias table, because two tables describing one fact become two competing truths the
/// moment they disagree. Learned rows carry Source = "LeadReviewLearned", which is
/// deliberately absent from <c>CustomerIdentityMaintenance.ManagedSources</c>, so a later
/// customer-profile synchronisation cannot expire what a person taught the platform.
///
/// Poisoning safeguards, all enforced here:
///   P1 self-identity  — never learn the tenant's own name / domains / the document's vendor block
///   P2 synthetic      — never learn Nexora's ingestion placeholders; never a free-mail Domain
///   P3 exclusivity    — Email/ErpAccount/TaxRegistration are exclusive; on conflict SKIP, never steal
///   P4 multi-owner    — Alias/Domain/PortalAccount may point at several customers; the
///                       resolver then returns AMBIGUOUS, which is an outcome, not an error
///   P5 reversal       — a later review that CHANGES the customer expires everything this
///                       lead taught about the rejected one before teaching afresh
///   P6 approval gate  — only "approve" + an explicitly supplied customer; NEVER a machine
///                       AUTO_MATCHED result (that is the path by which one machine mistake
///                       would bootstrap itself into an authoritative alias)
///   P7 single level   — identifiers point DIRECTLY at a CustomerId; no alias -> alias
///                       indirection, no supersession chain, so cycles are structurally
///                       impossible. If customer merge/supersession is ever added, port the
///                       `visited` HashSet cycle detector from ProductIdentityResolver
///                       (Inventory/Commercial/CommercialInventoryServices.cs:36-56) FIRST.
/// </summary>
public sealed class CustomerAliasLearner : ICustomerAliasLearner
{
    public const string SkipSelfIdentity = "selfIdentity";
    public const string SkipSynthetic = "syntheticIdentity";
    public const string SkipAliasConflict = "ALIAS_CONFLICT";
    public const string SkipNoEvidence = "noEvidence";

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<CustomerAliasLearner>? _log;

    public CustomerAliasLearner(ErpRfqAutomationContext db, ILogger<CustomerAliasLearner>? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<CustomerAliasLearningResult> LearnFromReviewAsync(
        long businessUnitId,
        Lead lead,
        long customerId,
        long? previousCustomerId,
        long? reviewAuditId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));

        // P6: only a human decision teaches. A machine AUTO_MATCH must never become an alias.
        if (!LeadCustomerMatchStatuses.IsHumanDecided(lead.CustomerMatchStatus))
            return CustomerAliasLearningResult.None;

        var now = DateTime.UtcNow;
        var skips = new List<string>();
        var expired = 0;

        // P5: the reviewer moved this lead to a different client. Everything this lead
        // previously taught about the rejected client is now known to be wrong.
        if (previousCustomerId.HasValue && previousCustomerId.Value != customerId)
        {
            var contradicted = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .Where(i => i.BusinessUnitId == businessUnitId
                            && i.CustomerId == previousCustomerId.Value
                            && i.LearnedFromLeadId == lead.Id
                            && i.EffectiveTo == null)
                .ToListAsync(ct);
            foreach (var identifier in contradicted)
            {
                identifier.EffectiveTo = now;
                expired++;
            }
        }

        var selfNameKeys = await LoadTenantSelfNameKeysAsync(businessUnitId, lead, ct);
        var selfDomains = await LoadTenantSelfDomainsAsync(businessUnitId, ct);

        var proposals = new List<Proposal>();

        // 1. Real sender + the buyer address printed on the document. For a folder-ingested
        //    or scanned bid the printed buyer address is the ONLY place the buying
        //    organisation's real domain appears, and the reviewer has just confirmed whose
        //    document it is — so both are learned identically.
        foreach (var raw in new[] { ResolveSender(lead), lead.CustomerBuyerEmailExtracted })
        {
            var address = LeadCustomerResolutionService.ParseAddress(raw);
            if (string.IsNullOrEmpty(address)) continue;
            var domain = RoutingValueNormalizer.DomainFromEmail(address);
            if (SyntheticIdentityGuard.IsSyntheticDomain(domain)) { skips.Add(SkipSynthetic); continue; }
            if (domain is not null && selfDomains.Contains(domain)) { skips.Add(SkipSelfIdentity); continue; }

            proposals.Add(new Proposal(CustomerIdentifierType.Email, address, address, true, 1.00m));
            if (domain is { Length: > 0 } && !SyntheticIdentityGuard.IsFreeMailDomain(domain))
                proposals.Add(new Proposal(CustomerIdentifierType.Domain, domain, domain, true, 0.95m));
            else if (domain is { Length: > 0 })
                skips.Add(SkipSynthetic);
        }

        // 2. The buying organisation's name, as printed.
        if (!string.IsNullOrWhiteSpace(lead.CustomerCompanyNameExtracted))
        {
            var display = lead.CustomerCompanyNameExtracted!.Trim();
            var key = CustomerNameNormalizer.LooseKey(display);
            if (key.Length == 0 || SelfIdentityGuard.IsSelfName(key, selfNameKeys)) skips.Add(SkipSelfIdentity);
            else proposals.Add(new Proposal(CustomerIdentifierType.Alias, key, display, true, 0.90m));
        }

        // 3. Portal + OUR vendor code at that portal. The pair is unique per customer and
        //    perfectly learnable; our vendor code ALONE identifies us, never them, so it is
        //    never learned on its own.
        if (!string.IsNullOrWhiteSpace(lead.CustomerPortalNameExtracted)
            && !string.IsNullOrWhiteSpace(lead.SupplierAccountRefOnDocument))
        {
            var portalKey = CustomerNameNormalizer.LooseKey(lead.CustomerPortalNameExtracted);
            var accountKey = RoutingValueNormalizer.Normalize(
                CustomerIdentifierType.ErpAccount, lead.SupplierAccountRefOnDocument!);
            if (portalKey.Length > 0 && accountKey.Length > 0)
                proposals.Add(new Proposal(
                    CustomerIdentifierType.PortalAccount,
                    $"{portalKey}|{accountKey}",
                    $"{lead.CustomerPortalNameExtracted!.Trim()} / {lead.SupplierAccountRefOnDocument!.Trim()}",
                    true, 0.92m));
        }

        // 4. The RFQ-number SHAPE. Suggestion-grade FOREVER (IsVerified = false): the
        //    learned-alias tier requires IsVerified, so a numbering convention can propose a
        //    client but can never link one.
        var pattern = RfqNumberPattern.Derive(lead.Rfqno);
        if (pattern is not null)
            proposals.Add(new Proposal(
                CustomerIdentifierType.RfqNumberPattern, pattern, lead.Rfqno!.Trim(), false, 0.50m));

        if (proposals.Count == 0)
        {
            skips.Add(SkipNoEvidence);
            return new CustomerAliasLearningResult(0, 0, expired, skips);
        }

        // P3: authoritative types are exclusive per tenant. Reuse the store's own advisory
        // locking + exclusivity check rather than racing it — and on conflict SKIP the value,
        // never steal it from the customer that already owns it.
        var authoritative = proposals
            .Where(p => p.Type is CustomerIdentifierType.Email or CustomerIdentifierType.Phone)
            .Select(p => (p.Type, Value: (string?)p.DisplayValue))
            .ToList();
        var authoritativeBlocked = false;
        if (authoritative.Count > 0)
        {
            try
            {
                await CustomerIdentityMaintenance.EnsureAuthoritativeValuesAvailableAsync(
                    _db, businessUnitId, customerId, authoritative, ct);
            }
            catch (CustomerIdentityConflictException)
            {
                authoritativeBlocked = true;
                skips.Add(SkipAliasConflict);
                _log?.LogInformation(
                    "Client alias learning skipped an authoritative value for lead {LeadId}: it already belongs to another customer in tenant {BusinessUnitId}.",
                    lead.Id, businessUnitId);
            }
        }

        var learned = 0;
        var reinforced = 0;
        foreach (var proposal in proposals)
        {
            if (authoritativeBlocked && proposal.Type is CustomerIdentifierType.Email or CustomerIdentifierType.Phone)
                continue;

            var current = await _db.Set<CustomerIdentifier>().IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.BusinessUnitId == businessUnitId
                                          && i.IdentifierType == proposal.Type
                                          && i.NormalizedValue == proposal.NormalizedValue
                                          && i.CustomerId == customerId
                                          && i.EffectiveTo == null, ct);
            if (current is not null)
            {
                current.ObservationCount += 1;
                current.LastObservedOn = now;
                // A human just re-confirmed it: a suggestion-grade row may be promoted, but
                // an RFQ-number SHAPE stays unverified by design.
                if (proposal.IsVerified && proposal.Type != CustomerIdentifierType.RfqNumberPattern)
                {
                    current.IsVerified = true;
                    if (current.Confidence < proposal.Confidence) current.Confidence = proposal.Confidence;
                }
                reinforced++;
                continue;
            }

            _db.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = businessUnitId,
                CustomerId = customerId,
                IdentifierType = proposal.Type,
                NormalizedValue = proposal.NormalizedValue,
                DisplayValue = Truncate(proposal.DisplayValue, 320),
                IsVerified = proposal.IsVerified,
                Confidence = proposal.Confidence,
                Source = CustomerIdentifierSources.LeadReviewLearned,
                EffectiveFrom = now,
                LearnedFromLeadId = lead.Id,
                LearnedFromReviewAuditId = reviewAuditId,
                ObservationCount = 1,
                LastObservedOn = now
            });
            learned++;
        }

        return new CustomerAliasLearningResult(learned, reinforced, expired, skips);
    }

    private static string? ResolveSender(Lead lead)
        => !string.IsNullOrWhiteSpace(lead.EmailIngests?.FromEmail)
            ? lead.EmailIngests!.FromEmail
            : lead.Clientemail;

    private async Task<HashSet<string>> LoadTenantSelfNameKeysAsync(
        long businessUnitId, Lead lead, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var businessUnitName = await _db.BusinessUnits.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitName)
            .SingleOrDefaultAsync(ct);
        Add(businessUnitName);
        // The document's own vendor/Vendname block names US, never the buyer.
        Add(lead.SupplierNameOnDocument);
        return keys;

        void Add(string? value)
        {
            var key = CustomerNameNormalizer.LooseKey(value);
            if (key.Length > 0) keys.Add(key);
        }
    }

    private async Task<HashSet<string>> LoadTenantSelfDomainsAsync(long businessUnitId, CancellationToken ct)
    {
        var mailboxes = await _db.EmailConfigurations.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.EmailAddress != null)
            .Select(c => c.EmailAddress!)
            .Distinct()
            .Take(50)
            .ToListAsync(ct);
        return mailboxes
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private sealed record Proposal(
        CustomerIdentifierType Type,
        string NormalizedValue,
        string DisplayValue,
        bool IsVerified,
        decimal Confidence);
}
