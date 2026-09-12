using System.Net.Mail;
using System.Data;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.LeadIdentity;
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
        // The customer/contact projection and the immutable commercial revision are one fact.
        // If revision creation (including lineage rebinding) fails, never leave the mutable Lead
        // addressed to a customer its current revision does not contain.
        if (_db.Database.CurrentTransaction is not null)
            return await ResolveAndAppendRevisionAsync(businessUnitId, leadId, ct);

        // Production enables Npgsql retry-on-failure. A user transaction must be created inside
        // its execution strategy or every real resolution fails before the first statement.
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            var outcome = await ResolveAndAppendRevisionAsync(businessUnitId, leadId, ct);
            await transaction.CommitAsync(ct);
            return outcome;
        });
    }

    private async Task<ClientResolutionOutcome> ResolveAndAppendRevisionAsync(
        long businessUnitId, long leadId, CancellationToken ct)
    {
        var lead = await _db.Leads
            .Include(l => l.LeadItems)
            .Include(l => l.EmailIngests)
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Id == leadId, ct)
            ?? throw new KeyNotFoundException($"Lead {leadId} was not found in business unit {businessUnitId}.");

        var establishedRevisionId = lead.CurrentRevisionId;
        var establishedIdentity = establishedRevisionId.HasValue
            ? await _db.Set<LeadRevision>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == establishedRevisionId.Value)
                .Select(x => new { x.CustomerIdSnapshot, x.ContactIdSnapshot })
                .SingleOrDefaultAsync(ct)
            : null;

        var outcome = await ResolveCoreAsync(businessUnitId, lead, ct);
        await _db.SaveChangesAsync(ct);
        // Client resolution happens after canonical identity reconciliation by design. When it
        // changes the customer/contact that RFQ promotion will inherit, record a new immutable
        // commercial revision immediately; otherwise the participation decision would point at
        // a snapshot that says one customer while the formal RFQ is addressed to another.
        if (establishedRevisionId.HasValue && establishedIdentity is not null
            && (establishedIdentity.CustomerIdSnapshot != lead.CustomerId
                || establishedIdentity.ContactIdSnapshot != lead.ContactId))
        {
            var identityKey = $"customer-resolution-revision:{businessUnitId}:{lead.Id}:"
                + $"{establishedRevisionId.Value}:{lead.CustomerId?.ToString() ?? "none"}:"
                + $"{lead.ContactId?.ToString() ?? "none"}";
            await new LeadIdentityApplicationService(_db).AppendHumanRevisionAsync(
                businessUnitId, lead.Id, "customer-resolution",
                $"Deterministic client resolution: {outcome.ReasonCode}.", identityKey, ct);
        }
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
        var ingestSubject = lead.EmailIngests?.EmailSubject;
        if (string.IsNullOrWhiteSpace(ingestFrom) && lead.EmailIngestsId.HasValue)
        {
            // SEC-ING-01: the tenant predicate is explicit because IgnoreQueryFilters removed the
            // only other one. This lookup was `x.Id == lead.EmailIngestsId` and NOTHING else — the
            // one call in this file (of ten) with no business-unit clause — and it is reachable
            // from the mailbox poller, which until now ran with a null tenant under the BYPASSRLS
            // pipeline role. EmailIngests carries no tenant column of its own, so the predicate
            // goes through the owning mailbox exactly as the table's RLS policy does.
            // The subject rides along on the SAME row read: it is evidence too (below), and a
            // second round trip for a column we already have in hand would be pure waste.
            var ingest = await _db.EmailIngests.AsNoTracking().IgnoreQueryFilters()
                .Where(x => x.Id == lead.EmailIngestsId.Value
                            && x.EmailConfiguration.BusinessUnitId == businessUnitId)
                .Select(x => new { x.FromEmail, x.EmailSubject })
                .SingleOrDefaultAsync(ct);
            ingestFrom = ingest?.FromEmail;
            ingestSubject ??= ingest?.EmailSubject;
        }

        var sender = ParseAddress(ingestFrom) ?? ParseAddress(lead.Clientemail);
        // THE NAME ON THE MAILBOX, WHICH WAS PARSED AND THROWN AWAY. EmailService stores the
        // sender as the mail client wrote it — "SEC Procurement <noreply@portal.se.com.sa>" —
        // and this method kept the address and discarded the words in front of it. On a
        // portal-relayed request that display name is frequently the ONLY unambiguous
        // statement of the buying organisation anywhere in the message: the relay domain names
        // the postman, the document header names nobody, and the human who configured the
        // mailbox typed the buyer's name by hand. It is kept now and read as a header.
        var senderDisplayName = ParseDisplayName(ingestFrom) ?? ParseDisplayName(lead.Clientemail);

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

        // The direction-of-trade firewall, applied to the two passages that come off the
        // ENVELOPE rather than off the document. A forwarded or replied-to message carries OUR
        // OWN trading name in the display name ("ALI ZAID AL QURAISHI Sales <sales@...>") and in
        // the subject line, and neither is a statement about a buyer. It matters more than it
        // used to: a header passage that names a company now demotes what the delivery address
        // says, so our own name arriving at header strength would suppress the one place a
        // portal print writes the buyer (lead 680 carried nothing but
        // "Saudi Electricity Company-DAMMAM"). The resolver applies this same guard to every
        // candidate NAME; this applies it to the two values before they become evidence at all.
        var selfNameKeys = selfNames
            .Select(CustomerNameNormalizer.LooseKey)
            .Where(key => key.Length > 0)
            .ToList();
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(senderDisplayName), selfNameKeys))
            senderDisplayName = null;
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(ingestSubject), selfNameKeys))
            ingestSubject = null;

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
            Passages = Passages(lead, senderDisplayName, ingestSubject),
            TenantSelfNameKeys = selfNames,
            TenantSelfDomains = tenantMailboxes
        };
    }

    /// <summary>
    /// Labels an extracted column can carry that state WHO IS BUYING. An SAP or portal print
    /// puts the strongest name on the page behind one of these — "Sold-to party",
    /// "Ordering party", "Purchaser" — and until now every one of them fell through to item
    /// text and was worth 0.70, while "Storage location" linked the lead at 0.88. The weakest
    /// label on the page outranked the strongest, which is exactly backwards.
    /// </summary>
    private static readonly string[] BuyerHeaderLabelWords =
    [
        "sold to", "soldto", "bill to", "billto", "buyer", "purchaser", "customer", "client",
        "ordering party", "company", "organisation", "organization", "account name", "end user",
        "requisitioner", "purchasing organisation", "purchasing organization", "purchasing group"
    ];

    /// <summary>
    /// Labels that state WHERE THE GOODS GO. Usually the buyer's own site, but a consignee is a
    /// fact about delivery and not about who is paying — see <see cref="PassageRole.ShipTo"/>.
    /// </summary>
    private static readonly string[] ShipToLabelWords =
    [
        "location", "deliver", "ship", "site", "plant", "consignee", "receiving", "warehouse",
        "depot", "substation", "works", "area", "region"
    ];

    /// <summary>
    /// What an extracted column label says its value IS. Separators are printing, not meaning,
    /// so "Sold-to party", "SOLD_TO PARTY" and "soldto party" are one label.
    ///
    /// A label that speaks BOTH vocabularies ("Ship-to customer", "Customer plant", "Delivery
    /// site of the buyer") is read as a ship-to, deliberately. Reading a consignee as the buyer
    /// is the expensive mistake this module exists to avoid — an EPC contractor buying for
    /// Saudi Aramco prints "Deliver to: Saudi Aramco" and is not Aramco — and a header now
    /// outranks the address, so a misread ship-to would not merely add a wrong name, it would
    /// suppress the right one.
    /// </summary>
    internal static PassageRole RoleForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return PassageRole.ItemText;
        var folded = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        var normalized = string.Join(' ', folded.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return PassageRole.ItemText;

        foreach (var word in ShipToLabelWords)
            if (normalized.Contains(word, StringComparison.Ordinal)) return PassageRole.ShipTo;
        foreach (var word in BuyerHeaderLabelWords)
            if (normalized.Contains(word, StringComparison.Ordinal)) return PassageRole.BuyerHeader;
        return PassageRole.ItemText;
    }

    /// <summary>
    /// The places a buyer writes its own name on a request, whatever printed it, and what each
    /// place is DOING on the page: a header states who is buying, a delivery address states
    /// where the goods go, item text merely mentions a name that may belong to anyone. Bounded,
    /// because a 1,500-line print states a location on every line.
    ///
    /// THE BUDGET IS SPENT IN ROLE ORDER, NOT PAGE ORDER. It used to fill item by item from the
    /// top of the document: on the 1,500-line Aramco bid list the 120 slots were gone around
    /// item 30, the delivery address and every company-name field after it never reached the
    /// evidence at all, and whether the buyer was found came down to which line the extractor
    /// happened to emit first. Collect in three passes; fill every header first, then every
    /// ship-to, then item text with whatever budget is left.
    /// </summary>
    internal static IReadOnlyList<DocumentPassage> Passages(
        Lead lead, string? senderDisplayName = null, string? emailSubject = null)
    {
        const int maxPassages = 120;
        const int maxChars = 600;
        var headers = new List<DocumentPassage>();
        var shipTos = new List<DocumentPassage>();
        var itemTexts = new List<DocumentPassage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string where, string? text, PassageRole role)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var bucket = role switch
            {
                PassageRole.BuyerHeader => headers,
                PassageRole.ShipTo => shipTos,
                _ => itemTexts
            };
            // Each bucket is bounded on its own, so a long bid list cannot make this method
            // allocate without limit before the budget below is applied.
            if (bucket.Count >= maxPassages) return;
            var trimmed = text.Trim();
            if (trimmed.Length > maxChars) trimmed = trimmed[..maxChars];
            if (!seen.Add(trimmed)) return;
            // NamesTheBuyer keeps exactly the meaning it has always had — true for anything that
            // is ABOUT the buyer — so every tier that reads that flag behaves as before. Role is
            // the finer statement laid on top of it.
            bucket.Add(new DocumentPassage(where, trimmed, role != PassageRole.ItemText) { Role = role });
        }

        bool Full() => headers.Count >= maxPassages
                       && shipTos.Count >= maxPassages
                       && itemTexts.Count >= maxPassages;

        // The two strongest statements on the page, and until recently the weakest evidence in
        // the engine. The company-name field was compared only for an exact key match and then
        // fuzzily, so "Saudi Aramco Ras Tanura Refinery" written there resolved to NOTHING while
        // the identical string in the delivery address linked at 0.88. And the short verbatim
        // sentence the extractor captures precisely because it names the buying organisation
        // ("MARAFIQ invites bidders in accordance with our Request for Quotation") was written to
        // the database and read by nothing in the product. Both are about the buyer by definition.
        Add("company named on the document", lead.CustomerCompanyNameExtracted, PassageRole.BuyerHeader);
        Add("the sentence that names the buyer", lead.CustomerCompanyEvidence, PassageRole.BuyerHeader);
        // A person typed the mailbox display name on purpose, and on a portal-relayed request
        // ("SEC Procurement <noreply@portal.se.com.sa>") it is the only unambiguous statement of
        // the buyer anywhere in the message. The subject is written by a person too, but it as
        // often names a third party as the sender ("re: Aramco spec for your SEC bid"), so it is
        // worth no more than any other incidental mention.
        Add("the name on the sender's mailbox", senderDisplayName, PassageRole.BuyerHeader);
        Add("the e-mail subject", emailSubject, PassageRole.ItemText);
        Add("delivery address", lead.DeliveryLocation, PassageRole.ShipTo);
        foreach (var item in lead.LeadItems ?? [])
        {
            if (Full()) break;
            Add("storage location", item.StorageLocation, PassageRole.ShipTo);
            var extra = ExtraFieldsJson.Deserialize(item.ExtraFields);
            if (extra is not null)
            {
                foreach (var (label, value) in extra)
                {
                    var role = RoleForLabel(label);
                    Add(role == PassageRole.ItemText ? "item text" : $"{label.ToLowerInvariant()} field",
                        value, role);
                }
            }
            Add("item text", item.ItemText, PassageRole.ItemText);
            Add("item text", item.MaterialPotext, PassageRole.ItemText);
        }

        var ordered = new List<DocumentPassage>(maxPassages);
        foreach (var passage in headers.Concat(shipTos).Concat(itemTexts))
        {
            if (ordered.Count >= maxPassages) break;
            ordered.Add(passage);
        }
        return ordered;
    }

    /// <summary>
    /// The words in front of the angle brackets: "SEC Procurement" out of
    /// "SEC Procurement &lt;noreply@portal.se.com.sa&gt;". Null when there are none, or when the
    /// client simply repeated the address there, which states nothing the parsed address does not.
    /// </summary>
    internal static string? ParseDisplayName(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (!MailAddress.TryCreate(trimmed, out var parsed)) return null;
        var display = parsed.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(display)) return null;
        return display.Contains('@') ? null : display;
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
        // Taught names are only worth loading when there is document text to search them for.
        var searchNames = evidence.Passages.Count > 0;

        // ONE ORDERED, SEPARATELY CAPPED QUERY PER CLASS OF IDENTIFIER.
        //
        // This was a single OR-predicate with one Take(500) and no OrderBy. Two things were
        // wrong with that, and they are the same thing twice. A database is free to return any
        // 500 rows that satisfy an unordered query — the plan may change with the statistics,
        // so the SAME lead against the SAME data could resolve differently on Tuesday, and this
        // module's whole promise to a rep is that the answer is derived, repeatable and
        // explainable. And because one cap covered every class at once, the broad class ate the
        // narrow one: past roughly 1,500 learned name rows in a tenant, the single row carrying
        // the buyer's exact sender address could be cut from the result, turning a 1.00
        // auto-link into a 0.65 "we think it might be" that a human then has to decide.
        //
        // Each class now has its own ORDER BY "Id" and its own budget, so a tenant that has
        // taught the platform thousands of aliases cannot crowd out its own authoritative
        // identifiers, and the set of rows is a function of the data alone.
        const int maxAuthoritativeIdentifiers = 200;
        const int maxExactNameIdentifiers = 100;
        const int maxScannedNameIdentifiers = 500;
        const int maxPortalAccountIdentifiers = 50;
        const int maxRfqPatternIdentifiers = 300;

        // Only active customers of this tenant can ever be linked.
        IQueryable<CustomerIdentifier> LiveIdentifiers() => _db.Set<CustomerIdentifier>()
            .AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.BusinessUnitId == businessUnitId && i.EffectiveTo == null)
            // IgnoreQueryFilters is a query-level flag set on this query; the subquery inherits it.
            .Where(i => _db.Customers.Any(c => c.Buid == businessUnitId && c.Id == i.CustomerId && c.IsActive != false));

        var identifiers = new List<CustomerIdentifierSnapshot>();
        var loadedIdentifierIds = new HashSet<long>();

        async Task LoadIdentifiersAsync(IQueryable<CustomerIdentifier> query, int take)
        {
            var rows = await query
                .OrderBy(i => i.Id)
                .Select(i => new CustomerIdentifierSnapshot(
                    i.Id, i.CustomerId, i.IdentifierType, i.NormalizedValue, i.IsVerified, i.Confidence, i.Source))
                .Take(take)
                .ToListAsync(ct);
            // The classes overlap by construction (an exact name key is also a verified name),
            // so identity is the row id — a duplicated row would count as two pieces of
            // evidence for one customer and quietly inflate a tier.
            foreach (var row in rows)
                if (loadedIdentifierIds.Add(row.Id)) identifiers.Add(row);
        }

        // The authoritative classes: a value that identifies ONE organisation outright.
        if (addresses.Length + domains.Length + accounts.Length + taxRegistrations.Length > 0)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    (i.IdentifierType == CustomerIdentifierType.Email && addresses.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.Domain && domains.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.ErpAccount && accounts.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.TaxRegistration && taxRegistrations.Contains(i.NormalizedValue))),
                maxAuthoritativeIdentifiers);

        // The name the document states, matched exactly. Loaded ahead of the open-ended scan
        // below and on its own budget: it is the one name row that is ABOUT this document, and
        // it is the row the old single cap was most likely to throw away.
        if (nameKey.Length > 0)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    (i.IdentifierType == CustomerIdentifierType.Alias || i.IdentifierType == CustomerIdentifierType.CustomerName)
                    && i.NormalizedValue == nameKey),
                maxExactNameIdentifiers);

        // Every name a profile or a reviewer has taught, so the document's passages can be
        // searched for them. The broad class, and therefore the one that is capped hardest
        // relative to what it might return.
        if (searchNames)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    (i.IdentifierType == CustomerIdentifierType.Alias || i.IdentifierType == CustomerIdentifierType.CustomerName)
                    && i.IsVerified),
                maxScannedNameIdentifiers);

        if (portalAccountKey.Length > 0)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    i.IdentifierType == CustomerIdentifierType.PortalAccount && i.NormalizedValue == portalAccountKey),
                maxPortalAccountIdentifiers);

        // Patterns are matched in C# (the stored regex cannot run in SQL), so they are only
        // worth loading when this lead carries an RFQ number to match against.
        if (hasRfq)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i => i.IdentifierType == CustomerIdentifierType.RfqNumberPattern),
                maxRfqPatternIdentifiers);

        var customers = await LoadCustomerNamesAsync(businessUnitId, evidence, identifiers, ct);
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
        long businessUnitId, LeadClientEvidence evidence, List<CustomerIdentifierSnapshot> identifiers, CancellationToken ct)
    {
        var matchedIds = identifiers.Select(i => i.CustomerId).Distinct().ToArray();

        // The name scan is read-only and capped. Above the cap we pre-filter in SQL on a
        // leading-character bucket (no pg_trgm, no new Postgres extension). The filter is
        // deliberately WIDER than the comparison it feeds: LooseKey/TightKey cannot run in
        // SQL without a stored normalised column, so SQL narrows on the RAW leading
        // characters and the exact/fuzzy comparison still happens on the normalised keys in
        // C#. Customers already matched by an identifier are always included.
        //
        // The buckets come from the PASSAGES, not from the company-name field. The field was
        // the only source, and on the document this fallback exists for — a portal print with
        // no company-name field at all — it is null, so the filter collapsed to
        // already-matched-only and the passage tier, the one tier that reads a print like lead
        // 680, was handed an empty customer list and could match nothing. The comment here
        // used to claim no auto-link tier could be degraded by the fallback. One was, to zero.
        var query = _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false);

        var total = await query.CountAsync(ct);
        if (total > _policy.MaximumNameScanRows)
        {
            var prefixes = NameScanPrefixes(evidence).ToArray();
            query = prefixes.Length > 0
                ? query.Where(c => matchedIds.Contains(c.Id) || prefixes.Contains(c.Name.ToUpper().Substring(0, 2)))
                : query.Where(c => matchedIds.Contains(c.Id));
        }

        return await query
            .OrderBy(c => c.Id)
            .Select(c => new CustomerNameSnapshot(c.Id, c.Name))
            .Take(_policy.MaximumNameScanRows)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The leading two characters of every word a passage about the buyer states, which is the
    /// widest SQL-expressible net that still narrows a very large customer list. "SEC Materials
    /// West Plant-West Operating Area" yields SE, MA, WE, PL, OP, AR — and SE is what finds
    /// "Saudi Electricity Company".
    ///
    /// Words shorter than three characters are skipped: they are articles and unit codes, and
    /// two of them would pull back most of the table for nothing.
    /// </summary>
    internal static IReadOnlyList<string> NameScanPrefixes(LeadClientEvidence evidence)
    {
        const int prefixLength = 2;
        const int maxPrefixes = 40;
        var prefixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddPrefix(string word)
        {
            if (word.Length < 3 || prefixes.Count >= maxPrefixes) return;
            var prefix = word[..prefixLength].ToUpperInvariant();
            if (seen.Add(prefix)) prefixes.Add(prefix);
        }

        // What the old filter used, first, so this change can only ever WIDEN the net: a name
        // whose first two characters are the first two of the company-name field still matches.
        var fieldPrefix = new string((evidence.CustomerCompanyName ?? string.Empty)
            .Where(char.IsLetterOrDigit).Take(prefixLength).Select(char.ToUpperInvariant).ToArray());
        if (fieldPrefix.Length == prefixLength && seen.Add(fieldPrefix)) prefixes.Add(fieldPrefix);

        foreach (var passage in evidence.Passages)
        {
            if (passage.Role == PassageRole.ItemText) continue;
            var word = new System.Text.StringBuilder();
            foreach (var c in passage.Text)
            {
                if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
                if (word.Length > 0) { AddPrefix(word.ToString()); word.Clear(); }
            }
            if (word.Length > 0) AddPrefix(word.ToString());
            if (prefixes.Count >= maxPrefixes) break;
        }
        return prefixes;
    }

    private async Task<List<CustomerContactSnapshot>> LoadContactsAsync(
        long businessUnitId, string[] addresses, string? buyerPersonName,
        List<CustomerIdentifierSnapshot> identifiers, CancellationToken ct)
    {
        var matchedIds = identifiers.Select(i => i.CustomerId).Distinct().ToArray();
        var surname = CustomerNameNormalizer.LooseKey(buyerPersonName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.Length > 2);

        var live = _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.BusinessUnitId == businessUnitId && c.CustomerId != null && c.IsActive != false);

        var contacts = new List<CustomerContactSnapshot>();
        var loadedContactIds = new HashSet<long>();

        async Task LoadContactRowsAsync(IQueryable<Contact> query, int take)
        {
            var rows = await query
                .OrderBy(c => c.Id)
                .Select(c => new CustomerContactSnapshot(
                    c.Id, c.CustomerId!.Value, c.Email, c.FirstName, c.LastName))
                .Take(take)
                .ToListAsync(ct);
            foreach (var row in rows)
                if (loadedContactIds.Add(row.ContactId)) contacts.Add(row);
        }

        // The contacts we are surest about: of a customer an identifier already matched, or at
        // the very address this lead came from. Skipped entirely when the lead has neither,
        // which is the normal shape of a folder-ingested print — an always-false predicate is
        // still a round trip.
        if (matchedIds.Length > 0 || addresses.Length > 0)
            await LoadContactRowsAsync(
                live.Where(c => matchedIds.Contains(c.CustomerId!.Value)
                                || (c.Email != null && addresses.Contains(c.Email.ToLower()))),
                200);

        // THE SURNAME PREFILTER MISSED EVERY HYPHENATED SAUDI SURNAME, which is most of them.
        // The buyer is printed "3C2-AMER AL-DOSSARY", the last token of the normalised key is
        // DOSSARY, and the stored contact is "Al-Dossary": an equality test on LastName never
        // matched, so the contact tier written for exactly this SEC format could not fire at
        // all. Containment matches the way the two spellings actually differ — one side carries
        // the family prefix and the other does not.
        //
        // It is a SEPARATE query, ordered and capped on its own, for the same reason the
        // identifier classes are: containment is far broader than equality, and a common
        // surname fragment sharing one budget with the exact-address contacts would evict the
        // contact we are most sure about.
        if (surname != null)
            await LoadContactRowsAsync(live.Where(c => c.LastName.ToUpper().Contains(surname)), 50);

        return contacts;
    }

    private async Task<List<PriorSenderResolution>> LoadPriorSenderResolutionsAsync(
        long businessUnitId, long leadId, string[] addresses, CancellationToken ct)
    {
        if (addresses.Length == 0) return [];

        var rows = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId && l.Id != leadId
                        && l.CustomerId != null && l.Clientemail != null
                        && addresses.Contains(l.Clientemail.ToLower()))
            // Ordered for the same reason as every other corpus read: an unordered LIMIT lets
            // the database choose which precedents this lead gets to see.
            .OrderBy(l => l.Id)
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
