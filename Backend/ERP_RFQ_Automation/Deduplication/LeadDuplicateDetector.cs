using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Deduplication;

/// <summary>Outcome of one duplicate check (for logging / callers; not a wire shape).</summary>
public sealed record DuplicateCheckResult(
    bool Flagged,
    long? FlaggedLeadId,
    long? OriginalLeadId,
    string? Reason)
{
    public static readonly DuplicateCheckResult NotDuplicate = new(false, null, null, null);
}

/// <summary>
/// Detects likely duplicate leads and stamps the duplicate flag (WP-A3).
/// Never throws business-flow-breaking errors from call sites: callers wrap the
/// call in try/catch, and the internal notification step is itself best-effort.
/// </summary>
public interface ILeadDuplicateDetector
{
    /// <summary>
    /// Checks the given lead against recent leads in the same business unit and,
    /// when a likely duplicate pair is found, marks the NEWER lead of the pair
    /// as <c>suspected</c> (DuplicateOfLeadId = the older lead) and notifies the
    /// original lead's assignee (or a manager when unassigned).
    /// </summary>
    Task<DuplicateCheckResult> CheckAndFlagAsync(long leadId, long businessUnitId, CancellationToken ct = default);
}

/// <summary>
/// Rule set (a lead pair is a duplicate when either holds):
///  (a) normalized RFQ numbers match exactly (casefold + strip non-alphanumerics,
///      only when both are non-null), OR
///  (b) same customer key (lower(Clientemail), falling back to normalized
///      BuyersName when the email is null/placeholder) AND bid-closing dates
///      within 2 days of each other (or both null) AND line-item overlap >= 0.6,
///      where overlap = |intersection| / min(|A|,|B|) over the normalized union
///      of ManufacturerPartNumber, ItemMaterialCode and CustomerRfqno.
///
/// Candidate set: same BU, not rejected (LeadStatusId != 25), created within the
/// last 90 days, excluding the lead itself. Queries are bounded (projection-only,
/// capped) and item sets are fetched in one batched query.
/// </summary>
public sealed class LeadDuplicateDetector : ILeadDuplicateDetector
{
    private const int CandidateWindowDays = 90;
    private const int MaxCandidatesPerQuery = 500;
    private const double ItemOverlapThreshold = 0.6;
    private static readonly TimeSpan BidDateTolerance = TimeSpan.FromDays(2);

    private static readonly Regex NonAlphaNum = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly ErpRfqAutomationContext _context;
    private readonly INotificationService? _notifications;
    private readonly ILogger<LeadDuplicateDetector>? _logger;

    public LeadDuplicateDetector(
        ErpRfqAutomationContext context,
        INotificationService? notifications = null,
        ILogger<LeadDuplicateDetector>? logger = null)
    {
        _context = context;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<DuplicateCheckResult> CheckAndFlagAsync(long leadId, long businessUnitId, CancellationToken ct = default)
    {
        // Tracked load: we may stamp the flag on this very lead. Explicit BU
        // predicate so the detector behaves identically with or without a
        // tenant-scoped context (worker vs request scope).
        var lead = await _context.Leads
            .Include(l => l.LeadItems)
            .FirstOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId, ct);

        if (lead == null || lead.LeadStatusId == 25)
            return DuplicateCheckResult.NotDuplicate;

        // A lead already under (or past) duplicate review is not re-flagged.
        if (lead.DuplicateStatus is "suspected" or "confirmed")
            return new DuplicateCheckResult(true, lead.Id, lead.DuplicateOfLeadId, "Already flagged");

        var since = DateTime.UtcNow.AddDays(-CandidateWindowDays);

        var normRfq = Normalize(lead.Rfqno);
        var customerKey = CustomerKey(lead.Clientemail, lead.BuyersName);

        // ---------------- Rule (a): exact normalized RFQ number ----------------
        CandidateRow? match = null;
        string? reason = null;

        if (normRfq != null)
        {
            var rfqCandidates = await BaseCandidates(businessUnitId, leadId, since)
                .Where(l => l.Rfqno != null)
                .OrderByDescending(l => l.CreatedDate)
                .Take(MaxCandidatesPerQuery)
                .Select(l => new CandidateRow(l.Id, l.Rfqno, l.Clientemail, l.BuyersName, l.BidClosingDate,
                                              l.CreatedDate, l.DuplicateStatus, l.DuplicateOfLeadId, l.AssignTo))
                .ToListAsync(ct);

            match = rfqCandidates
                .Where(c => Normalize(c.Rfqno) == normRfq)
                .OrderBy(c => c.CreatedDate)
                .ThenBy(c => c.Id)
                .FirstOrDefault();
            if (match != null)
                reason = "Same RFQ number";
        }

        // ------------- Rule (b): same customer + close dates + items -----------
        if (match == null && customerKey != null && lead.LeadItems.Count > 0)
        {
            var triggerItemKeys = ItemKeys(lead.LeadItems.Select(li =>
                (li.ManufacturerPartNumber, li.ItemMaterialCode, li.CustomerRfqno)));

            if (triggerItemKeys.Count > 0)
            {
                var customerCandidates = await BaseCandidates(businessUnitId, leadId, since)
                    .Where(l => l.Clientemail != null || l.BuyersName != null)
                    .OrderByDescending(l => l.CreatedDate)
                    .Take(MaxCandidatesPerQuery)
                    .Select(l => new CandidateRow(l.Id, l.Rfqno, l.Clientemail, l.BuyersName, l.BidClosingDate,
                                                  l.CreatedDate, l.DuplicateStatus, l.DuplicateOfLeadId, l.AssignTo))
                    .ToListAsync(ct);

                var survivors = customerCandidates
                    .Where(c => CustomerKey(c.Clientemail, c.BuyersName) == customerKey)
                    .Where(c => DatesClose(lead.BidClosingDate, c.BidClosingDate))
                    .ToList();

                if (survivors.Count > 0)
                {
                    // One batched fetch of every surviving candidate's item keys.
                    var survivorIds = survivors.Select(s => s.Id).ToList();
                    var candidateItems = await _context.LeadItems.AsNoTracking()
                        .Where(li => survivorIds.Contains(li.LeadId))
                        .Select(li => new { li.LeadId, li.ManufacturerPartNumber, li.ItemMaterialCode, li.CustomerRfqno })
                        .ToListAsync(ct);

                    var keysByLead = candidateItems
                        .GroupBy(i => i.LeadId)
                        .ToDictionary(
                            g => g.Key,
                            g => ItemKeys(g.Select(i => (i.ManufacturerPartNumber, i.ItemMaterialCode, i.CustomerRfqno))));

                    match = survivors
                        .Where(c => keysByLead.TryGetValue(c.Id, out var keys)
                                    && Overlap(triggerItemKeys, keys) >= ItemOverlapThreshold)
                        .OrderBy(c => c.CreatedDate)
                        .ThenBy(c => c.Id)
                        .FirstOrDefault();
                    if (match != null)
                        reason = "Same customer, close deadline and matching items";
                }
            }
        }

        if (match == null)
            return DuplicateCheckResult.NotDuplicate;

        // ----------------- Stamp the NEWER lead of the pair --------------------
        var triggerIsNewer = lead.CreatedDate > match.CreatedDate
                             || (lead.CreatedDate == match.CreatedDate && lead.Id > match.Id);

        long newerId = triggerIsNewer ? lead.Id : match.Id;
        long olderId = triggerIsNewer ? match.Id : lead.Id;

        Lead? newer = triggerIsNewer
            ? lead
            : await _context.Leads.FirstOrDefaultAsync(l => l.Id == newerId && l.BusinessUnitId == businessUnitId, ct);
        if (newer == null)
            return DuplicateCheckResult.NotDuplicate;

        // Respect existing review state on the lead we are about to flag.
        if (newer.DuplicateStatus is "suspected" or "confirmed")
            return new DuplicateCheckResult(true, newer.Id, newer.DuplicateOfLeadId, "Already flagged");
        if (newer.DuplicateStatus == "not_duplicate" && newer.DuplicateOfLeadId == olderId)
            return DuplicateCheckResult.NotDuplicate; // a human already cleared this exact pair

        newer.DuplicateStatus = "suspected";
        newer.DuplicateOfLeadId = olderId;
        newer.DuplicateResolvedBy = null;
        newer.ModifiedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "Lead {NewerId} flagged as suspected duplicate of lead {OlderId} ({Reason}). BU={Bu}",
            newerId, olderId, reason, businessUnitId);

        // Best-effort notification — never breaks detection or the caller.
        try
        {
            await NotifyAsync(newerId, olderId, businessUnitId, reason!, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "Duplicate-lead notification failed for lead {NewerId} (original {OlderId}); flag was still saved.",
                newerId, olderId);
        }

        return new DuplicateCheckResult(true, newerId, olderId, reason);
    }

    // =========================================================== notification

    private async Task NotifyAsync(long duplicateLeadId, long originalLeadId, long businessUnitId, string reason, CancellationToken ct)
    {
        if (_notifications == null) return;

        var original = await _context.Leads.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == originalLeadId && l.BusinessUnitId == businessUnitId, ct);
        var duplicate = await _context.Leads.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == duplicateLeadId && l.BusinessUnitId == businessUnitId, ct);
        if (original == null || duplicate == null) return;

        // Recipient: the original lead's assignee; otherwise a manager/admin of the BU.
        User? recipient = null;
        if (original.AssignTo.HasValue)
            recipient = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == original.AssignTo.Value, ct);

        recipient ??= await _context.Users.AsNoTracking()
            .Where(u => u.Buid == businessUnitId && u.IsActive == true && u.RoleId != null)
            .Join(_context.SetupMasters.AsNoTracking().Where(s => s.SetupType.ToLower() == "role"),
                  u => u.RoleId, s => (long?)s.SetupId, (u, s) => new { User = u, Role = s })
            .Where(x => x.Role.SetupValue.ToLower().Contains("admin")
                        || x.Role.SetupValue.ToLower().Contains("manager")
                        || (x.Role.SetupCode != null && (x.Role.SetupCode.ToLower().Contains("admin")
                                                         || x.Role.SetupCode.ToLower().Contains("manager"))))
            .OrderBy(x => x.User.Id)
            .Select(x => x.User)
            .FirstOrDefaultAsync(ct);

        if (recipient == null || string.IsNullOrWhiteSpace(recipient.Email))
        {
            _logger?.LogWarning(
                "No recipient found for duplicate-lead notification (original {OriginalId}, BU {Bu}).",
                originalLeadId, businessUnitId);
            return;
        }

        await _notifications.NotifyDuplicateLeadAsync(new DuplicateLeadNotification
        {
            ToEmail = recipient.Email,
            ToName = $"{recipient.FirstName} {recipient.LastName}".Trim(),
            RecipientName = recipient.FirstName,
            OriginalRfqNumber = original.Rfqno ?? $"#{original.Id}",
            DuplicateRfqNumber = duplicate.Rfqno ?? $"#{duplicate.Id}",
            BuyerName = duplicate.BuyersName ?? original.BuyersName ?? "Unknown buyer",
            Reason = reason,
            BusinessUnitId = businessUnitId.ToString(),
            CtaPath = $"/procurement/leads/view/{duplicateLeadId}"
        }, ct);
    }

    // ================================================================ helpers

    private IQueryable<Lead> BaseCandidates(long businessUnitId, long selfId, DateTime since) =>
        _context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId
                        && l.Id != selfId
                        && (l.LeadStatusId == null || l.LeadStatusId != 25)
                        && l.CreatedDate > since);

    private sealed record CandidateRow(
        long Id,
        string? Rfqno,
        string? Clientemail,
        string? BuyersName,
        DateTime? BidClosingDate,
        DateTime CreatedDate,
        string? DuplicateStatus,
        long? DuplicateOfLeadId,
        long? AssignTo);

    /// <summary>Casefold + strip everything that is not a-z / 0-9. Null when nothing is left.</summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = NonAlphaNum.Replace(raw.ToLowerInvariant(), "");
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Customer identity key: lowercased Clientemail when it is a real customer
    /// address, else the normalized BuyersName. Internal pipeline placeholder
    /// addresses (extraction@pipeline.local etc.) identify the pipeline, not a
    /// customer, so they must never act as a shared key across all extracted leads.
    /// </summary>
    internal static string? CustomerKey(string? email, string? buyersName)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(e)
            && !e.EndsWith("@pipeline.local", StringComparison.Ordinal)
            && e != "system@rfq.com")
            return "e:" + e;

        var n = Normalize(buyersName);
        return n == null ? null : "n:" + n;
    }

    private static bool DatesClose(DateTime? a, DateTime? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return (a.Value - b.Value).Duration() <= BidDateTolerance;
    }

    /// <summary>Normalized union of MPN + material code + customer RFQ no over a lead's items.</summary>
    internal static HashSet<string> ItemKeys(IEnumerable<(string? Mpn, string? MaterialCode, string? CustomerRfqno)> items)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mpn, code, custRfq) in items)
        {
            if (Normalize(mpn) is { } m) keys.Add("m:" + m);
            if (Normalize(code) is { } c) keys.Add("c:" + c);
            if (Normalize(custRfq) is { } r) keys.Add("r:" + r);
        }
        return keys;
    }

    /// <summary>|intersection| / min(|A|,|B|); 0 when either side is empty.</summary>
    internal static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return (double)inter / Math.Min(a.Count, b.Count);
    }
}
