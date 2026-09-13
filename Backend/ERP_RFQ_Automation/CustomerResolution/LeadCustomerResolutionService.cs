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
        var (corpus, tenantSharedAcronyms) = await LoadCorpusAndSharedAcronymsAsync(businessUnitId, evidence, ct);
        // The tenant-wide shared initials go in beside the corpus: above the name-scan cap the
        // corpus is a filtered slice, and initials that look unique in a slice can belong to two
        // customers in the tenant.
        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, _policy, tenantSharedAcronyms);

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

        var businessUnitName = await _db.BusinessUnits.AsNoTracking().IgnoreQueryFilters()
            .Where(b => b.Id == businessUnitId)
            .Select(b => b.BusinessUnitName)
            .SingleOrDefaultAsync(ct);
        // WHAT "US" MEANS FOR AN ADDRESS: the ingestion mailboxes AND the domains the tenant's
        // own staff write from. This read was the mailboxes alone, and the people who forward
        // bids are not mailboxes: rfq@alquraishi.com is the mailbox, ahmed@alquraishi.com.sa is
        // the salesman. His forward of an SEC bid then arrived from a domain that was not "us",
        // so a Domain row somebody once taught against alquraishi.com.sa linked every later
        // forward from any colleague to that customer at 0.95, whatever the attachment said, and
        // his own name on the envelope became evidence about the buyer. One loader, shared with
        // the learner and routing, so the three can never disagree about who we are.
        var tenantSelfDomains = (await TenantSelfIdentity.LoadSelfDomainsAsync(_db, businessUnitId, ct))
            // Ordered so the evidence handed to the resolver is the same on every pass.
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToList();

        // THE NAME ON THE MAILBOX, WHICH WAS PARSED AND THROWN AWAY. EmailService stores the
        // sender as the mail client wrote it — "Saudi Aramco <ordersender-prod@ansmtp.ariba.com>"
        // — and this method kept the address and discarded the words in front of it. It is kept
        // now, and SenderDisplayName decides what it is allowed to say: read after the tenant's
        // own domains are known, because a rep forwarding a bid puts OUR name on it.
        //
        // WHO "WE" ARE IS TWO LISTS, AND THEY ARE NOT THE SAME LIST (T09c). The tenant's configured
        // name says which MAILBOXES are ours: a domain that spells it is a colleague's. The vendor block
        // the document prints says which NAMES are ours, and nothing about addresses, because it is read
        // off the page by an extractor that can be wrong. Both lived in one list, and that list became
        // the evidence's TenantSelfNameKeys and the self test on the envelope: a Marafiq RFQ whose vendor
        // block was misread as "MARAFIQ" made marafiq.com.sa "ours", so buyer@marafiq.com.sa, registered
        // on Marafiq, resolved to NO_EVIDENCE where the same message with the field empty linked at 1.00.
        // The vendor block still reaches the resolver on its own field, which suppresses it as a name.
        // The vendor block still counts for addresses where it IS a spelling of the configured name
        // (TenantSelfIdentity.DomainSelfNames), the one rule routing, the resolver's Guard and the learner ask.
        var tenantNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(businessUnitName)) tenantNames.Add(businessUnitName!);
        var domainSelfNames = TenantSelfIdentity.DomainSelfNames(tenantNames, lead.SupplierNameOnDocument);
        var selfNames = new List<string>(tenantNames);
        if (!string.IsNullOrWhiteSpace(lead.SupplierNameOnDocument)) selfNames.Add(lead.SupplierNameOnDocument!);

        var envelope = SenderDisplayName(ingestFrom, tenantSelfDomains, domainSelfNames);
        if (envelope.Name is null) envelope = SenderDisplayName(lead.Clientemail, tenantSelfDomains, domainSelfNames);
        var senderDisplayName = envelope.Name;

        // The direction-of-trade firewall, applied to the two passages that come off the
        // ENVELOPE rather than off the document. A forwarded or replied-to message carries OUR
        // OWN trading name in the display name ("ALI ZAID AL QURAISHI Sales <sales@...>") and in
        // the subject line, and neither is a statement about a buyer. The resolver already
        // refuses any candidate NAME that is ours; this refuses the two values before they
        // become evidence at all, so no tier that reads passages has to remember to.
        var selfNameKeys = selfNames
            .Select(CustomerNameNormalizer.LooseKey)
            .Where(key => key.Length > 0)
            .ToList();
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(senderDisplayName), selfNameKeys))
            senderDisplayName = null;
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(ingestSubject), selfNameKeys))
            ingestSubject = null;

        // The organisation the mailbox is signed with, read by the same rules as the display name.
        var senderOrganisation = SenderOrganisationName(ingestFrom, tenantSelfDomains, domainSelfNames)
                                 ?? SenderOrganisationName(lead.Clientemail, tenantSelfDomains, domainSelfNames);
        if (SelfIdentityGuard.IsSelfName(CustomerNameNormalizer.LooseKey(senderOrganisation), selfNameKeys))
            senderOrganisation = null;

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
            SenderOrganisationName = senderOrganisation,
            Passages = Passages(lead, senderDisplayName, ingestSubject, envelope.Role),
            TenantSelfNameKeys = tenantNames,
            TenantSelfDomains = tenantSelfDomains
        };
    }

    /// <summary>
    /// Labels an extracted column can carry that state WHICH ORGANISATION IS BUYING. An SAP or
    /// portal print puts the strongest name on the page behind one of these — "Sold-to party",
    /// "Ordering party", "Purchaser" — and until now every one of them fell through to item
    /// text and was worth 0.70, while "Storage location" linked the lead at 0.88. The weakest
    /// label on the page outranked the strongest, which is exactly backwards.
    ///
    /// "buyer" is here only for the organisation forms ("Buyer organisation", "Buyer company");
    /// a bare "Buyer" column is a person and is caught by <see cref="PersonRoleLabelWords"/>
    /// first.
    ///
    /// ONLY LABELS THAT NAME THE PAYING PARTY BY DEFINITION. "Company", "Organisation" and "Account
    /// name" were here too, and so was every label that merely contains them ("Owner company", "Make /
    /// Company", "Brand company"). Those columns name whoever the spreadsheet was about: the cable maker
    /// on a BOQ, the site owner on an EPC requisition (in Saudi Aramco contract documents COMPANY is
    /// Aramco and CONTRACTOR the EPC), the account a rep keeps. A header links and speaks against the
    /// delivery address, so "Make / Company: Saudi Cable Company" took lead 680's own shape from SEC to
    /// Saudi Cable at 0.88, and "Company: Saudi Aramco" on a Hyundai requisition linked Aramco while
    /// Hyundai sat on the books with a contact at the sending domain. They are item text again, which is
    /// what they were before labels were read at all.
    /// </summary>
    private static readonly string[] BuyerHeaderLabelWords =
    [
        "sold to", "soldto", "bill to", "billto", "buyer", "purchaser", "ordering party",
        "purchasing organisation", "purchasing organization"
    ];

    /// <summary>
    /// Labels whose column is filled with PEOPLE. CanonicalRfqNormalizer copies raw spreadsheet
    /// headings into ExtraFields, so a "Requisitioner" column of "Ahmed Al-Ghamdi" was read as a
    /// header — link strength, and a header skips the consignee check. The resolver scans one-word
    /// trade names, and in this market those are family names: "Al-Ghamdi Trading Est" keys to
    /// GHAMDI, so that one cell joined Saudi Electricity Company in the linking set and an SEC
    /// print that linked on its delivery address went AMBIGUOUS and asked a person. A name in a
    /// person's column is a mention, offered and never decided.
    /// </summary>
    private static readonly string[] PersonRoleLabelWords =
    [
        "requisitioner", "requisitioned by", "requestor", "requester", "requested by", "buyer",
        "purchasing group", "contact", "attention", "attn", "prepared by", "approved by",
        "created by", "raised by", "originator", "planner", "expeditor", "person", "focal point"
    ];

    /// <summary>
    /// A person label that also names an organisation ("Buyer organisation", "Buyer company") is
    /// about the organisation. Checked before the person words, so the qualifier wins.
    /// </summary>
    private static readonly string[] OrganisationQualifierWords =
    [
        "organisation", "organization", "company", "party", "entity", "firm"
    ];

    /// <summary>
    /// Labels for parties who are by definition NOT the buyer: us, the maker of the goods. A
    /// "Manufacturer company" column of Siemens is a statement about who made a valve, and read
    /// as a header it linked the lead to a Siemens customer record at 0.88.
    /// </summary>
    private static readonly string[] NotTheBuyerLabelWords =
    [
        "vendor", "supplier", "manufacturer", "maker", "bidder"
    ];

    /// <summary>
    /// The same parties, by words that are only safe to compare whole: "make" sits inside "makeup" and
    /// "oem" inside "phoenix"-style product names. "Make / Company" and "Brand company" name the maker.
    /// </summary>
    private static readonly HashSet<string> NotTheBuyerWholeWords = new(StringComparer.Ordinal)
    {
        "make", "brand", "oem", "mfr", "mfg"
    };

    /// <summary>
    /// Labels whose value is a code, a number or a date, never an organisation's name: "Customer
    /// material number", "Sold-to party no.". Whole words only, because "no" is inside every
    /// other word. Without this a customer part number printed "SEC-4411002233" under a customer
    /// label carried SEC's initials into a linking passage.
    /// </summary>
    private static readonly HashSet<string> CodeValueLabelWords = new(StringComparer.Ordinal)
    {
        "number", "no", "nr", "num", "code", "id", "ref", "date", "qty", "quantity", "price"
    };

    /// <summary>
    /// Labels that state WHERE THE GOODS GO, or FOR WHOM THE JOB IS DONE. Usually the buyer's own
    /// site, but a consignee is a fact about delivery and not about who is paying — see
    /// <see cref="PassageRole.ShipTo"/>.
    ///
    /// "END USER", "CLIENT", "CUSTOMER" AND "PROJECT OWNER" ARE NOT HERE, AND NOT AMONG THE HEADERS.
    /// On a contractor's requisition those columns name the project owner, not a party the goods go
    /// to or the party paying. As a header (#10) "End User: Saudi Aramco" on Hyundai E&amp;C's hdec.com
    /// requisition skipped the consignee check and linked Aramco at 0.88. As a ship-to (T10) it was
    /// still link strength: Al-Babtain delivering to its own yard with "Client: Saudi Electricity
    /// Company" on the line put SEC beside Al-Babtain at 0.88 and the lead went AMBIGUOUS, where the
    /// yard alone links Al-Babtain. They are item text: offered to a rep, never linking and never
    /// demoting. A label that also says where the goods go ("Ship-to customer", "Customer plant",
    /// "End user site") is still a ship-to by that word.
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
    /// Read in order, most cautious first: a party that is not the buyer, a code, a person, a
    /// ship-to, and only then a header. A header is link strength and skips the consignee check,
    /// so every doubt about a label resolves toward the role that asks a person.
    ///
    /// A label that speaks BOTH the ship-to and header vocabularies ("Ship-to customer", "Customer
    /// plant", "Delivery site of the buyer") is read as a ship-to, deliberately. Reading a
    /// consignee as the buyer is the expensive mistake this module exists to avoid — an EPC
    /// contractor buying for Saudi Aramco prints "Deliver to: Saudi Aramco" and is not Aramco —
    /// and a header now outranks the address, so a misread ship-to would not merely add a wrong
    /// name, it would suppress the right one.
    /// </summary>
    internal static PassageRole RoleForLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return PassageRole.ItemText;
        var folded = new string(label.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var normalized = string.Join(' ', words);
        if (normalized.Length == 0) return PassageRole.ItemText;

        bool Mentions(IEnumerable<string> vocabulary) =>
            vocabulary.Any(word => normalized.Contains(word, StringComparison.Ordinal));

        if (Mentions(NotTheBuyerLabelWords) || words.Any(NotTheBuyerWholeWords.Contains)) return PassageRole.ItemText;
        if (words.Any(CodeValueLabelWords.Contains)) return PassageRole.ItemText;
        if (Mentions(PersonRoleLabelWords) && !Mentions(OrganisationQualifierWords)) return PassageRole.ItemText;
        if (Mentions(ShipToLabelWords)) return PassageRole.ShipTo;
        if (Mentions(BuyerHeaderLabelWords)) return PassageRole.BuyerHeader;
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
    /// <param name="senderDisplayNameRole">
    /// What the mailbox display name may say, as decided by <see cref="SenderDisplayName"/>.
    /// Defaults to item text, the role that can never link a lead on its own, so a caller that
    /// forgets to ask cannot turn a person's name into a header.
    /// </param>
    internal static IReadOnlyList<DocumentPassage> Passages(
        Lead lead, string? senderDisplayName = null, string? emailSubject = null,
        PassageRole senderDisplayNameRole = PassageRole.ItemText)
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
        // On a portal relay or a no-reply mailbox ("SEC Procurement <noreply@portal.se.com.sa>")
        // the display name is the organisation, and can be the only statement of the buyer in the
        // whole message; on a person's mailbox it is that person. SenderDisplayName tells the two
        // apart. The subject is written by a person too, but it as often names a third party as
        // the sender ("re: Aramco spec for your SEC bid"), so it is worth no more than any other
        // incidental mention.
        Add(DocumentPassage.SenderDisplayNameWhere, senderDisplayName, senderDisplayNameRole);
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
    /// The mailbox display name, and what it is allowed to say about who is buying.
    ///
    /// A HEADER ONLY WHERE IT CANNOT BE A PERSON'S NAME. It was first read as a header — link
    /// strength, the same weight as the company-name field — on every message. Nearly every real
    /// message is "Firstname Lastname &lt;person@company&gt;", and the resolver now scans one-word
    /// trade names of four letters or more, which in this market are very often family names:
    /// Al-Rashid Trading keys to RASHID, Al-Nasser Group to NASSER. "Rashid Al-Otaibi
    /// &lt;r.otaibi@se.com.sa&gt;" would have linked an SEC enquiry to Al-Rashid Trading at 0.88 and
    /// asked nobody. <see cref="PassageRole.BuyerHeader"/> is what a document states about who is
    /// buying; a person's own name on their own mailbox is not that.
    ///
    /// So the name is a header in one place only: on a PORTAL RELAY, which writes the buyer
    /// organisation into the display name because its own domain names only the postman — and
    /// even there, only when the name does not look like a person's (see
    /// <see cref="LooksLikeAPersonsName"/>). Anywhere else it is a mention — offered to a rep,
    /// never decided for them.
    ///
    /// A NO-REPLY MAILBOX IS NOT ENOUGH. It was the second place a name became a header, on the
    /// reasoning that nobody answers a no-reply mailbox so the name on it cannot be a person's.
    /// It very often is: SharePoint and OneDrive send "Rashid Al-Otaibi
    /// &lt;no-reply@sharepointonline.com&gt;" when a buyer shares an RFQ file, and that linked the
    /// SEC enquiry to Al-Rashid Trading at 0.88 — the exact hazard this method was written to
    /// close, reopened through a different mailbox. And where it is not a person it is the
    /// software's brand ("Microsoft Teams &lt;noreply@teams.microsoft.com&gt;"), which is nobody's
    /// buyer either. The organisation's own no-reply mailbox loses nothing that matters: its
    /// domain is evidence on its own tier, and its name is still offered as a mention.
    ///
    /// A mailbox the resolver refuses as evidence at all (Nexora's own ingestion labels, or the
    /// tenant's own domain or a host under it when a rep forwards a bid) contributes no name,
    /// because the name on it is ours. That mirrors the resolver's Guard exactly.
    /// </summary>
    /// <param name="tenantSelfDomains">
    /// The tenant's own domains or mailbox addresses, in either shape
    /// (<see cref="TenantSelfIdentity.LoadSelfDomainsAsync"/>).
    /// </param>
    /// <param name="tenantSelfNames">
    /// The tenant's configured names (the business unit's), and the document's vendor block only where it
    /// is a spelling of one of them (<see cref="TenantSelfIdentity.DomainSelfNames"/>). Never the vendor block
    /// as extracted: a misread one made the buyer's own domain ours (T09c). A domain whose
    /// name spells one of them is ours too (<see cref="TenantSelfIdentity.IsOurs(string?, IEnumerable{string}?, IEnumerable{string?}?)"/>),
    /// which is how the resolver's Guard now reads it; without them this answered from the domain list
    /// alone and a colleague with no Nexora login still put his name on the page.
    /// </param>
    internal static (string? Name, PassageRole Role) SenderDisplayName(
        string? from, IEnumerable<string> tenantSelfDomains, IEnumerable<string?>? tenantSelfNames = null)
    {
        var name = ParseDisplayName(from);
        var address = ParseAddress(from);
        var domain = RoutingValueNormalizer.DomainFromEmail(address);
        if (name is null || address is null || string.IsNullOrWhiteSpace(domain)
            || SyntheticIdentityGuard.IsSyntheticDomain(domain))
            return (null, PassageRole.ItemText);

        if (TenantSelfIdentity.IsOurs(domain, tenantSelfDomains, tenantSelfNames))
            return (null, PassageRole.ItemText);

        return SyntheticIdentityGuard.IsPortalRelayDomain(domain) && !LooksLikeAPersonsName(name)
            ? (name, PassageRole.BuyerHeader)
            : (name, PassageRole.ItemText);
    }

    /// <summary>
    /// The organisation the sender's own mailbox is signed with, the buying-function words taken off:
    /// "Hyundai E&amp;C" out of "Hyundai E&amp;C Procurement &lt;procurement@hdec.com&gt;". Null wherever the
    /// name cannot speak for an organisation: no display name, a person's name
    /// (<see cref="LooksLikeAPersonsName"/>), a relay (its name is already a header, see
    /// <see cref="SenderDisplayName"/>), a consumer mailbox, our own mail or Nexora's placeholders, or
    /// nothing left once the function words are gone.
    ///
    /// WHY IT IS READ (#14). Hyundai E&amp;C mails from hdec.com about an Aramco site. With Hyundai on the
    /// books but no contact or Domain row at hdec.com, nothing on the page was a claim for Hyundai, so
    /// Aramco linked on its site address at 0.88 and Hyundai was never offered. The display name was on
    /// the page all along, as a mention that named nobody. The resolver now reads it as a claim for the
    /// one customer it reads as, which can only demote a consignee and offer the writer; it never links
    /// anybody by itself.
    /// </summary>
    internal static string? SenderOrganisationName(
        string? from, IEnumerable<string> tenantSelfDomains, IEnumerable<string?>? tenantSelfNames = null)
    {
        var selfDomains = tenantSelfDomains as IReadOnlyCollection<string> ?? tenantSelfDomains.ToList();
        var (name, role) = SenderDisplayName(from, selfDomains, tenantSelfNames);
        if (name is null || role != PassageRole.ItemText) return null;
        var domain = RoutingValueNormalizer.DomainFromEmail(ParseAddress(from));
        if (!IdentityDomainGuard.IsOrganisationDomain(domain, selfDomains)) return null;
        // ONE WORD STILL SIGNS HERE (T04). The one-word person reading exists for the relay header,
        // where the error is a link. A signature can only demote a consignee and offer the writer, so the
        // error on this side is the opposite one: "Hyundai <procurement@hdec.com>" read as a person would
        // let Aramco link on its site address at 0.88. A doubtful word keeps asking a person.
        if (LooksLikeAPersonsName(name) && !IsOneWord(name)) return null;

        var kept = name
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !SignatureFunctionWords.Contains(token.Trim('.', ',', '"', '\'', '(', ')').ToUpperInvariant()));
        var signed = string.Join(' ', kept).Trim().Trim('-', ',', '/', '|', ':').Trim();
        return CustomerNameNormalizer.LooseKey(signed).Length == 0 ? null : signed;
    }

    /// <summary>
    /// What a buying function calls itself on its mailbox, taken off a signature so the organisation's
    /// own name is left. Narrower than <see cref="OrganisationNameWords"/> on purpose: SERVICES, SUPPLY,
    /// SYSTEM or NETWORK are part of many company names ("Aramco Services Company") and stay on.
    /// </summary>
    internal static readonly HashSet<string> SignatureFunctionWords = new(StringComparer.Ordinal)
    {
        "PROCUREMENT", "PURCHASING", "PURCHASE", "TENDER", "TENDERS", "TENDERING", "SOURCING",
        "CONTRACTS", "CONTRACTING", "BIDS", "BIDDING", "RFQ", "RFP", "NOTIFICATION", "NOTIFICATIONS",
        "TEAM", "OFFICE", "DEPARTMENT", "DEPT", "DIVISION", "DESK", "ADMIN", "ADMINISTRATION",
        "MATERIALS", "ORDERS", "BUYING", "المشتريات"
    };

    /// <summary>
    /// Words that make a display name an organisation's rather than a person's: what a buying
    /// function calls itself ("Procurement", "Tenders"), legal form, sector, and the geography
    /// every national company carries ("Saudi Aramco" is two capitalised words and is not a
    /// person). A closed list, so it errs one way only: a relay name it does not recognise
    /// ("Petro Rabigh") is read as a person and offered as a mention at 0.70 instead of linking,
    /// which costs a rep one click; the other error links a lead to the wrong customer.
    /// </summary>
    private static readonly HashSet<string> OrganisationNameWords = new(StringComparer.Ordinal)
    {
        // What a buying function or a system calls itself.
        "PROCUREMENT", "PURCHASING", "PURCHASE", "TENDER", "TENDERS", "TENDERING", "SOURCING",
        "SUPPLY", "SUPPLIES", "CHAIN", "CONTRACTS", "CONTRACTING", "BIDS", "BIDDING", "RFQ", "RFP",
        "NOTIFICATION", "NOTIFICATIONS", "PORTAL", "SYSTEM", "NETWORK", "TEAM", "OFFICE",
        "DEPARTMENT", "DEPT", "DIVISION", "SERVICES", "SERVICE", "DESK", "CENTER", "CENTRE",
        "ADMIN", "ADMINISTRATION", "MATERIALS", "MANAGEMENT", "OPERATIONS", "ORDERS", "BUYING",
        // Legal form.
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "LTD", "LIMITED", "LLC", "PLC", "WLL",
        "EST", "ESTABLISHMENT", "GROUP", "HOLDING", "HOLDINGS", "GMBH", "AG", "JSC",
        // Sector.
        "ELECTRICITY", "ELECTRIC", "WATER", "POWER", "ENERGY", "OIL", "GAS", "PETROLEUM",
        "PETROCHEMICAL", "PETROCHEMICALS", "CHEMICAL", "CHEMICALS", "REFINERY", "REFINING",
        "CEMENT", "STEEL", "MINING", "INDUSTRIES", "INDUSTRIAL", "ENGINEERING", "CONSTRUCTION",
        "TRADING", "AUTHORITY", "MINISTRY", "MUNICIPALITY", "COMMISSION", "AGENCY", "UNIVERSITY",
        "HOSPITAL", "AIRLINES", "UTILITIES", "TELECOM", "BANK", "PORTS",
        // Geography every national or international company carries.
        "SAUDI", "ARABIA", "ARABIAN", "ARAB", "KSA", "KINGDOM", "NATIONAL", "GULF", "MIDDLE",
        "EAST", "INTERNATIONAL", "GLOBAL", "GENERAL", "UNITED", "EMIRATES",
        // Arabic: company, establishment, procurement, administration, group.
        "شركة", "الشركة", "مؤسسة", "المؤسسة", "المشتريات", "إدارة", "ادارة", "مجموعة"
    };

    /// <summary>
    /// Whether a mailbox display name reads as a person rather than an organisation.
    ///
    /// Three shapes are a person. "Rashid Al-Otaibi via Coupa" — a portal names the individual who
    /// acted through it. A Firstname Lastname shape: two to four words made only of letters (hyphen,
    /// apostrophe and dot allowed inside), none of them an organisation word and none an acronym
    /// written in capitals inside a mixed-case name ("SEC Procurement"). And ONE word in mixed case
    /// made only of letters with no organisation word in it ("Rashid", "Al-Rashid").
    ///
    /// THE ONE WORD (T04). A single word was never a person, so "Rashid &lt;noreply@ansmtp.ariba.com&gt;"
    /// was a relay header, and "Rashid" is the whole one-word key of Al-Rashid Trading: the header was
    /// the statement, and the lead linked to the trading house at 0.88 on a buyer's first name. A word
    /// written entirely in capitals ("SABIC", "SEC") is an organisation's initials and stays one.
    /// THE COST: a mixed-case one-word trade name on a relay ("Marafiq &lt;...ariba...&gt;") is offered at
    /// 0.70 instead of linking, and so is any single word in a script with no capitals. Anything with a
    /// digit or an ampersand ("Hyundai E&amp;C"), and five words or more, are not a person's name.
    /// </summary>
    internal static bool LooksLikeAPersonsName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var tokens = displayName.Trim().Trim('"', '\'').Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Any(token => string.Equals(token, "via", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (tokens.Length == 1) return IsOneWordPersonsName(tokens[0]);
        if (tokens.Length > 4) return false;

        var writtenInCapitals = displayName.Where(char.IsLetter).All(char.IsUpper);
        foreach (var raw in tokens)
        {
            var token = raw.Trim('.', ',', '(', ')', '"', '\'');
            if (token.Length == 0) return false;
            if (!token.All(c => char.IsLetter(c) || c is '-' or '\'' or '.' or '’')) return false;
            foreach (var part in token.Split(['-', '\'', '.', '’'], StringSplitOptions.RemoveEmptyEntries))
                if (OrganisationNameWords.Contains(part.ToUpperInvariant())) return false;
            var letters = token.Where(char.IsLetter).ToArray();
            if (!writtenInCapitals && letters.Length is >= 2 and <= 6 && letters.All(char.IsUpper))
                return false;
        }
        return true;
    }

    /// <summary>One word read as a person: see <see cref="LooksLikeAPersonsName"/>.</summary>
    private static bool IsOneWordPersonsName(string raw)
    {
        var token = raw.Trim('.', ',', '(', ')', '"', '\'');
        if (token.Length == 0) return false;
        if (!token.All(c => char.IsLetter(c) || c is '-' or '\'' or '.' or '’')) return false;
        var letters = token.Where(char.IsLetter).ToArray();
        if (letters.Length == 0 || letters.All(char.IsUpper)) return false;
        foreach (var part in token.Split(['-', '\'', '.', '’'], StringSplitOptions.RemoveEmptyEntries))
            if (OrganisationNameWords.Contains(part.ToUpperInvariant())) return false;
        return true;
    }

    private static bool IsOneWord(string name)
        => name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length == 1;

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

    internal async Task<ClientResolutionCorpus> LoadCorpusAsync(
        long businessUnitId, LeadClientEvidence evidence, CancellationToken ct)
        => (await LoadCorpusAndSharedAcronymsAsync(businessUnitId, evidence, ct)).Corpus;

    private async Task<(ClientResolutionCorpus Corpus, IReadOnlySet<string> TenantSharedAcronyms)>
        LoadCorpusAndSharedAcronymsAsync(long businessUnitId, LeadClientEvidence evidence, CancellationToken ct)
    {
        var addresses = new[] { evidence.SenderEmail, evidence.DocumentBuyerEmail }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct().ToArray();
        var domains = addresses
            .Select(RoutingValueNormalizer.DomainFromEmail)
            // The same domains the resolver's Guard keeps: a relay's, a free-mail provider's or
            // our own Domain row can never match there, so it is not worth a read here.
            // Ours by the tenant's configured names, and the vendor block only where it spells one of them
            // (T09c): a vendor block misread as the buyer's name must not keep the buyer's own Domain rows out
            // of the corpus. The evidence overload, so this loader and the Guard cannot ask two ways.
            .Where(d => IdentityDomainGuard.IsOrganisationDomain(d, evidence.TenantSelfDomains)
                        && !TenantSelfIdentity.IsOurs(d, evidence))
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

        // A domain only a system mailbox on the page reaches is matched only by a Domain row a person entered, the
        // resolver's S2 rule; the rows it would refuse are not read. See CustomerIdentityResolver's domain tier.
        var personDomains = addresses
            .Where(address => !IdentityDomainGuard.IsSystemMailbox(address))
            .Select(RoutingValueNormalizer.DomainFromEmail)
            .Where(domain => domain is not null && domains.Contains(domain))
            .Select(domain => domain!)
            .Distinct()
            .ToArray();
        var systemOnlyDomains = domains.Except(personDomains).ToArray();
        var enteredByAPerson = CustomerIdentifierSources.EnteredByAPerson;

        // The authoritative classes: a value that identifies ONE organisation outright.
        if (addresses.Length + domains.Length + accounts.Length + taxRegistrations.Length > 0)
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    (i.IdentifierType == CustomerIdentifierType.Email && addresses.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.Domain && personDomains.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.Domain && systemOnlyDomains.Contains(i.NormalizedValue)
                     && enteredByAPerson.Contains(i.Source)) ||
                    (i.IdentifierType == CustomerIdentifierType.ErpAccount && accounts.Contains(i.NormalizedValue)) ||
                    (i.IdentifierType == CustomerIdentifierType.TaxRegistration && taxRegistrations.Contains(i.NormalizedValue))),
                maxAuthoritativeIdentifiers);

        // WHO ELSE WRITES FROM THIS DOMAIN. The resolver lets a delivery address link only while
        // nothing on the page names somebody else, and one thing that does is a mail domain another
        // customer's records write from: Hyundai E&C mails procurement@hdec.com about Aramco's Ras
        // Tanura site, and Hyundai's contact k.lee@hdec.com says whose domain that is. This loader only
        // ever read the EXACT sender address, so the rule was proven in unit tests that hand the
        // resolver k.lee's row directly and could never fire on a real lead: Aramco linked at 0.88.
        // So the page's organisation domains are read too, for verified Email rows (here), contacts
        // and earlier human decisions (below). Only when there are passages, because the rule only
        // speaks about a name found in one. Our own and shared domains were removed above.
        //
        // THE COST: an unindexed suffix match ("LIKE '%@hdec.com'") over this tenant's rows, per
        // organisation domain on the page (at most two), each read ordered and capped at the
        // learner's own bound. Human-paced for the learner; per ingested lead here, which a tenant's
        // row counts keep in the milliseconds. A stored domain column would make it an index lookup.
        var domainEvidence = evidence.Passages.Count > 0 ? domains : [];
        foreach (var domain in domainEvidence)
        {
            var atDomain = "@" + domain;
            await LoadIdentifiersAsync(
                LiveIdentifiers().Where(i =>
                    i.IdentifierType == CustomerIdentifierType.Email
                    && i.IsVerified
                    && i.Source != CustomerIdentifierSources.LeadReviewUnverified
                    && i.NormalizedValue.EndsWith(atDomain)),
                CustomerAliasLearner.MaximumDomainEvidenceRead);
        }

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

        var names = await LoadCustomerNamesAsync(businessUnitId, evidence, identifiers, ct);
        var contacts = await LoadContactsAsync(businessUnitId, addresses, domainEvidence, evidence.BuyerPersonName, identifiers, ct);
        var priorSenders = await LoadPriorSenderResolutionsAsync(businessUnitId, evidence.LeadId, addresses, domainEvidence, ct);

        return (new ClientResolutionCorpus
        {
            Identifiers = identifiers,
            Customers = names.Customers,
            Contacts = contacts,
            PriorSenderResolutions = priorSenders
        }, names.TenantAcronymCollisions);
    }

    /// <summary>The customers handed to the resolver, and the initials two or more of the
    /// tenant's active customers share (computed over the WHOLE tenant, not over
    /// <paramref name="Customers"/>).</summary>
    internal sealed record CustomerNameScan(
        IReadOnlyList<CustomerNameSnapshot> Customers, IReadOnlySet<string> TenantAcronymCollisions);

    /// <summary>
    /// A bound on the tenant-wide Id+Name read above the name-scan cap. Far past any customer
    /// list this product will hold; a tenant past it needs the stored acronym column (see
    /// <see cref="LoadCustomerNamesAsync"/>), because a customer beyond the bound can share
    /// initials with one inside it and the collision would go unseen.
    /// </summary>
    internal const int MaximumAcronymIndexRows = 100_000;

    /// <summary>A bound on how many customers the initials on a page may pin into the corpus.</summary>
    internal const int MaximumAcronymPinnedCustomers = 500;

    internal async Task<CustomerNameScan> LoadCustomerNamesAsync(
        long businessUnitId, LeadClientEvidence evidence, IReadOnlyList<CustomerIdentifierSnapshot> identifiers,
        CancellationToken ct)
    {
        var query = _db.Customers.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Buid == businessUnitId && c.IsActive != false);

        // At or under the cap every active customer is loaded, so initials the loaded list
        // shares are exactly the initials the tenant shares, and no second read is needed.
        var total = await query.CountAsync(ct);
        if (total <= _policy.MaximumNameScanRows)
        {
            var everyone = await query
                .OrderBy(c => c.Id)
                .Select(c => new CustomerNameSnapshot(c.Id, c.Name))
                .Take(_policy.MaximumNameScanRows)
                .ToListAsync(ct);
            return new CustomerNameScan(everyone, AcronymIndex.Build(everyone).Collisions);
        }

        // ABOVE THE CAP. The name scan is read-only and capped, so SQL pre-filters on a
        // leading-character bucket (no pg_trgm, no new Postgres extension). The filter is
        // deliberately WIDER than the comparison it feeds: LooseKey/TightKey cannot run in SQL
        // without a stored normalised column, so SQL narrows on the RAW leading characters and
        // the exact/fuzzy comparison still happens on the normalised keys in C#.
        //
        // The buckets come from the PASSAGES, not from the company-name field. The field was the
        // only source, and on the document this fallback exists for — a portal print with no
        // company-name field at all — it is null, so the filter collapsed to already-matched-only
        // and the passage tier, the one tier that reads a print like lead 680, was handed an
        // empty customer list and could match nothing.
        //
        // TWO SHAPES A BUCKET CAN NEVER SEE, and both lost the lead outright above the cap while
        // the same document linked below it:
        //
        //   * INITIALS. "SEC Materials West Plant-West Operating Area" yields SE, MA, WE, PL, OP,
        //     AR, and "Saudi Electricity Company" is filed under SA. The print that links at 0.85
        //     on SEC's initials in a 1,900-customer tenant resolved to nothing in a 2,100-customer
        //     one. Initials cannot be bucketed at all, so the customers whose initials are written
        //     on the page are found by name in C# (below) and pinned into the read.
        //   * THE ARTICLE. "Al-Rashid Trading" is filed under AL, the passage "Rashid Trading
        //     warehouse" yields RA, and a two-letter "Al" on the page is skipped as noise. The
        //     name's bucket is now also taken after a leading Al-/Al /El-/El /The.
        //
        // Customers already matched by an identifier, and customers whose initials are on the
        // page, are read FIRST, so a wide bucket can never push them past the cap.
        var pinned = identifiers.Select(i => i.CustomerId).ToHashSet();
        IReadOnlySet<string> collisions = new HashSet<string>(StringComparer.Ordinal);
        if (evidence.Passages.Count > 0)
        {
            // THE COST, stated plainly: one Id+Name read of every active customer in the tenant,
            // per resolution above the cap, and AcronymKey over each of them in C# — about a
            // megabyte and some tens of milliseconds at 20,000 customers, and a backfill pays it
            // once per lead. It buys two things the capped list cannot give. The customers whose
            // initials the page prints (above). And initials that two customers share, which
            // identify neither: "Saudi Cable Company" (bucket SA) and "Sudair Ceramics Company"
            // (bucket SU) both derive SCC, the page "SCC Store, Sudair Industrial City" buckets
            // only Sudair Ceramics, the capped list saw SCC as unique, and the lead auto-linked to
            // Sudair Ceramics at 0.85 — where, below the cap, the same page linked nobody. Whether
            // a lead links must not depend on how many customers the tenant has.
            //
            // OWNER DECISION: the durable fix is a stored, indexed acronym (and loose-key) column
            // on Customers, maintained on write, which turns both of these into an indexed lookup.
            // That is a schema change and was out of scope for this round.
            var index = AcronymIndex.Build(await query
                .OrderBy(c => c.Id)
                .Select(c => new CustomerNameSnapshot(c.Id, c.Name))
                .Take(MaximumAcronymIndexRows)
                .ToListAsync(ct));
            collisions = index.Collisions;
            var namedByInitials = new HashSet<long>();
            foreach (var word in PassageWords(evidence))
            {
                if (namedByInitials.Count >= MaximumAcronymPinnedCustomers) break;
                // EVERY owner of the initials, not the first: a customer left out here is a
                // collision the resolver cannot see, which is the defect this read exists to close.
                if (index.CustomersByAcronym.TryGetValue(word, out var owners)) namedByInitials.UnionWith(owners);
            }
            pinned.UnionWith(namedByInitials);
        }

        var pinnedIds = pinned.ToArray();
        var prefixes = NameScanPrefixes(evidence).ToArray();
        var filtered = prefixes.Length > 0
            ? query.Where(c => pinnedIds.Contains(c.Id)
                               || prefixes.Contains(c.Name.ToUpper().Substring(0, 2))
                               || ((c.Name.ToUpper().StartsWith("AL-") || c.Name.ToUpper().StartsWith("AL ")
                                    || c.Name.ToUpper().StartsWith("EL-") || c.Name.ToUpper().StartsWith("EL "))
                                   && prefixes.Contains(c.Name.ToUpper().Substring(3, 2)))
                               || (c.Name.ToUpper().StartsWith("THE ")
                                   && prefixes.Contains(c.Name.ToUpper().Substring(4, 2))))
            : query.Where(c => pinnedIds.Contains(c.Id));

        var customers = await filtered
            .OrderBy(c => pinnedIds.Contains(c.Id) ? 0 : 1)
            .ThenBy(c => c.Id)
            .Select(c => new CustomerNameSnapshot(c.Id, c.Name))
            .Take(_policy.MaximumNameScanRows)
            .ToListAsync(ct);
        return new CustomerNameScan(customers, collisions);
    }

    /// <summary>
    /// Every customer's derived initials (<see cref="CustomerNameNormalizer.AcronymKey"/>), and
    /// which of them two or more customers share. Built in C# because AcronymKey cannot run in
    /// SQL. "Shared" is the resolver's own rule (<see cref="CustomerIdentityResolver.SharedDerivedAcronyms"/>),
    /// not a second copy of it, so the loader and the resolver can never disagree about it.
    /// </summary>
    internal sealed record AcronymIndex(
        IReadOnlyDictionary<string, long[]> CustomersByAcronym, IReadOnlySet<string> Collisions)
    {
        public static AcronymIndex Build(IEnumerable<CustomerNameSnapshot> customers)
        {
            var list = customers as IReadOnlyList<CustomerNameSnapshot> ?? customers.ToList();
            var byAcronym = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (var customer in list)
            {
                // The resolver's remembered initials: the same AcronymKey, worked out once per name, because above
                // the cap this reads every customer in the tenant on every lead.
                var acronym = CustomerIdentityResolver.DerivedInitials(customer.Name);
                if (acronym.Length == 0) continue;
                if (!byAcronym.TryGetValue(acronym, out var owners))
                    byAcronym[acronym] = owners = [];
                owners.Add(customer.CustomerId);
            }
            return new AcronymIndex(
                byAcronym.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray(), StringComparer.Ordinal),
                CustomerIdentityResolver.SharedDerivedAcronyms(list));
        }
    }

    /// <summary>
    /// The words the resolver will search the page for initials in, exactly as it will see them:
    /// each passage's <see cref="CustomerNameNormalizer.LooseKey"/>, split on the spaces
    /// <see cref="CustomerIdentityResolver.ContainsWholeWords"/> matches between. Three to six
    /// letters, the only lengths AcronymKey produces. Every role, item text included, because the
    /// resolver scans initials in item text too (as a suggestion); a page read differently here
    /// would hand the resolver a customer list on which a shared acronym looks unique again. In
    /// role order, so the page's headers and addresses pin their customers before its item text.
    /// </summary>
    internal static IEnumerable<string> PassageWords(LeadClientEvidence evidence)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var passage in evidence.Passages
                     .OrderBy(p => p.Role switch { PassageRole.BuyerHeader => 0, PassageRole.ShipTo => 1, _ => 2 }))
        {
            foreach (var word in CustomerNameNormalizer.LooseKey(passage.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length is < 3 or > 6 || !word.All(c => c is >= 'A' and <= 'Z')) continue;
                if (seen.Add(word)) yield return word;
            }
        }
    }

    /// <summary>
    /// The leading two characters of every word a passage about the buyer states, which is the
    /// widest SQL-expressible net that still narrows a very large customer list. "Saudi
    /// Electricity Company-DAMMAM" yields SA, EL, CO, DA — and SA is what finds "Saudi Electricity
    /// Company". (This comment used to say SE from "SEC Materials West Plant" found it. It never
    /// did: that name is filed under SA. Initials are found by name instead — see
    /// <see cref="PassageWords"/>.)
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
        long businessUnitId, string[] addresses, string[] organisationDomains, string? buyerPersonName,
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

        // Contacts at the page's organisation domains, so the resolver can see whose domain the
        // sender's is when the sender is not a contact himself (see LoadCorpusAndSharedAcronymsAsync).
        // Active customers only: a rival offered to a rep must be a customer the rep can pick.
        foreach (var domain in organisationDomains)
        {
            var atDomain = "@" + domain;
            await LoadContactRowsAsync(
                live.Where(c => c.Email != null && c.Email.ToLower().EndsWith(atDomain))
                    .Where(c => _db.Customers.Any(x => x.Buid == businessUnitId && x.Id == c.CustomerId && x.IsActive != false)),
                CustomerAliasLearner.MaximumDomainEvidenceRead);
        }

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

    /// <summary>
    /// Every status <see cref="LeadCustomerMatchStatuses.IsHumanDecided"/> accepts, as a list SQL can
    /// filter on. The prior-sender reads capped FIRST and kept human decisions after (T08): on a domain
    /// with 205 newer machine links, the consignee's own older human decision fell past the cap, its tie
    /// was lost, and a sibling's contact demoted it. Filtering in the WHERE makes the cap count only rows
    /// that can be evidence.
    /// </summary>
    internal static readonly string[] HumanDecidedStatuses =
    [
        LeadCustomerMatchStatuses.Confirmed,
        LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved,
        "CUSTOMER_CONFIRMED",
        "VERIFIED"
    ];

    private async Task<List<PriorSenderResolution>> LoadPriorSenderResolutionsAsync(
        long businessUnitId, long leadId, string[] addresses, string[] organisationDomains, CancellationToken ct)
    {
        if (addresses.Length == 0) return [];

        var exact = await LoadExactPriorSenderResolutionsAsync(businessUnitId, leadId, addresses, ct);

        // Earlier leads a PERSON linked, from any address on the page's organisation domains: the
        // third fact that says whose domain it is (see LoadCorpusAndSharedAcronymsAsync). The same
        // two columns the learner reads for its own domain test, the sender and the buyer address the
        // document printed, because a folder-ingested print carries its buyer's domain only there.
        // The address is parsed out of "Name <a@b>" shapes, which the resolver compares by domain.
        var onDomains = new List<PriorSenderResolution>();
        foreach (var domain in organisationDomains)
        {
            var atDomain = "@" + domain;
            var bracketed = atDomain + ">";
            var rows = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
                .Where(l => l.BusinessUnitId == businessUnitId && l.Id != leadId && l.CustomerId != null
                            && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                            && ((l.Clientemail != null
                                 && (l.Clientemail.ToLower().EndsWith(atDomain) || l.Clientemail.ToLower().EndsWith(bracketed)))
                                || (l.CustomerBuyerEmailExtracted != null
                                    && (l.CustomerBuyerEmailExtracted.ToLower().EndsWith(atDomain)
                                        || l.CustomerBuyerEmailExtracted.ToLower().EndsWith(bracketed)))))
                .OrderByDescending(l => l.Id)
                .Select(l => new { l.Clientemail, l.CustomerBuyerEmailExtracted, CustomerId = l.CustomerId!.Value })
                .Take(CustomerAliasLearner.MaximumDomainEvidenceRead)
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                foreach (var raw in new[] { row.Clientemail, row.CustomerBuyerEmailExtracted })
                {
                    var address = ParseAddress(raw);
                    if (address is not null
                        && string.Equals(RoutingValueNormalizer.DomainFromEmail(address), domain, StringComparison.Ordinal))
                        onDomains.Add(new PriorSenderResolution(address, row.CustomerId));
                }
            }
        }

        return exact.Concat(onDomains).Distinct().ToList();
    }

    private async Task<List<PriorSenderResolution>> LoadExactPriorSenderResolutionsAsync(
        long businessUnitId, long leadId, string[] addresses, CancellationToken ct)
    {
        var rows = await _db.Leads.AsNoTracking().IgnoreQueryFilters()
            // Only a HUMAN-resolved precedent is worth suggesting from; suggesting from an earlier
            // machine guess would let one mistake propagate through the corpus. Filtered here, before
            // the cap, so 200 machine links cannot hide the person's decision (T08).
            .Where(l => l.BusinessUnitId == businessUnitId && l.Id != leadId
                        && l.CustomerId != null && l.Clientemail != null
                        && HumanDecidedStatuses.Contains(l.CustomerMatchStatus)
                        && addresses.Contains(l.Clientemail.ToLower()))
            // Ordered for the same reason as every other corpus read: an unordered LIMIT lets
            // the database choose which precedents this lead gets to see.
            .OrderBy(l => l.Id)
            .Select(l => new { Email = l.Clientemail!, CustomerId = l.CustomerId!.Value })
            .Take(200)
            .ToListAsync(ct);

        return rows
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
