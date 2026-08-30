using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public static class LifecycleStatusCatalog
{
    private static readonly (string Code, string Label)[] LeadStatuses =
    {
        ("RECEIVED", "Received"),
        ("PENDING_IDENTIFICATION", "Pending Identification"),
        ("UNASSIGNED", "Unassigned"),
        ("ASSIGNED", "Assigned"),
        ("UNDER_REVIEW", "Under Review"),
        ("QUALIFIED", "Qualified"),
        ("DISQUALIFIED", "Disqualified"),
        ("CONVERTED_TO_RFQ", "Converted to RFQ"),
        ("QUOTED", "Quoted"),
        ("NEGOTIATION", "Negotiation"),
        ("AWARDED", "Awarded"),
        ("PARTIALLY_AWARDED", "Partially Awarded"),
        ("LOST", "Lost"),
        ("CANCELLED", "Cancelled"),
        ("COMPLETED", "Completed"),
        ("DUPLICATED", "Duplicated")
    };

    private static readonly (string Code, string Label)[] RfqStatuses =
    {
        ("DRAFT", "Draft"),
        ("VALIDATING", "Validating"),
        ("NEEDS_REVIEW", "Needs Review"),
        ("READY_FOR_SALES", "Ready for Sales"),
        ("INVENTORY_CHECK", "Inventory Check"),
        ("SUPPLIER_SOURCING", "Supplier Sourcing"),
        ("AWAITING_SUPPLIER_RESPONSE", "Awaiting Supplier Response"),
        ("PRICING_COMPLETE", "Pricing Complete"),
        ("QUOTE_PREPARATION", "Quote Preparation"),
        ("QUOTE_SENT", "Quote Sent"),
        ("CUSTOMER_FOLLOW_UP", "Customer Follow-Up"),
        ("REVISION_REQUESTED", "Revision Requested"),
        ("AWARDED", "Awarded"),
        ("PARTIALLY_AWARDED", "Partially Awarded"),
        ("LOST", "Lost"),
        ("EXPIRED", "Expired"),
        ("CANCELLED", "Cancelled")
    };

    private static readonly (string Code, string Label)[] QuoteStatuses =
    {
        ("DRAFT", "Draft"),
        ("SENT", "Sent"),
        ("ACCEPTED", "Accepted"),
        ("REJECTED", "Rejected"),
        ("EXPIRED", "Expired"),
        ("ORDERED", "Ordered")
    };

    /// <summary>
    /// Order lifecycle. Seeded because <c>OrderService</c> RESOLVES these rows rather than
    /// treating them as configuration: converting a quote to an order looks up
    /// <c>OrderStatus/DRAFT</c> and throws <c>"No OrderStatus setup found in the system"</c> when
    /// it is absent (Services/OrderService.cs:132, :198, :281). A tenant provisioned without them
    /// works perfectly through lead → RFQ → quote and then fails at the first conversion — the
    /// exact moment the product proves its value — with an error the customer cannot self-repair.
    ///
    /// <para>Both CANCELED and CANCELLED are present deliberately: the codebase resolves both
    /// spellings, and seeding only one leaves whichever call site uses the other unresolvable.</para>
    /// </summary>
    private static readonly (string Code, string Label)[] OrderStatuses =
    {
        ("DRAFT", "Draft"),
        ("ORDERED", "Ordered"),
        ("CONFIRMED", "Confirmed"),
        ("SHIPPED", "Shipped"),
        ("DELIVERED", "Delivered"),
        ("COMPLETED", "Completed"),
        ("CANCELLED", "Cancelled"),
        ("CANCELED", "Canceled")
    };

    /// <summary>
    /// Payment lifecycle. <c>UNPAID</c> is resolved on every order creation
    /// (OrderService.cs:135, :201, :284) and by customer-award application
    /// (OrderToCash/CustomerAwardApplicationService.cs:666), so it is a hard dependency of the
    /// order path, not a reporting nicety.
    /// </summary>
    private static readonly (string Code, string Label)[] PaymentStatuses =
    {
        ("UNPAID", "Unpaid"),
        ("PARTIAL", "Partially paid"),
        ("PAID", "Paid")
    };

    public static IReadOnlyList<SetupMaster> CreateFor(BusinessUnit businessUnit, string actor, DateTime? now = null)
    {
        var createdOn = now ?? DateTime.UtcNow;
        return LeadStatuses.Select(item => Create("LeadStatus", item, businessUnit, actor, createdOn))
            .Concat(RfqStatuses.Select(item => Create("RFQStatus", item, businessUnit, actor, createdOn)))
            .Concat(QuoteStatuses.Select(item => Create("QuoteStatus", item, businessUnit, actor, createdOn)))
            .Concat(OrderStatuses.Select(item => Create("OrderStatus", item, businessUnit, actor, createdOn)))
            .Concat(PaymentStatuses.Select(item => Create("PaymentStatus", item, businessUnit, actor, createdOn)))
            .ToArray();
    }

    /// <summary>
    /// Adds every catalogue row this business unit does not already have, and reports how many.
    ///
    /// <para>Check-then-insert, never overwrite: a state the tenant already carries keeps its own
    /// label, and a second copy is never written — duplicates would put each state twice in every
    /// picker and give <see cref="ResolveIdAsync"/> two rows to choose between. Matched per
    /// (type, code) with the type folded for case and spacing and the code folded for case, the
    /// same comparison the original <c>Release01CommercialIdentity</c> seed used, so a tenant that
    /// spells the type "Quote Status" is recognised rather than duplicated.</para>
    ///
    /// <para>Rows are ADDED to the change tracker and NOT saved. The caller decides when to flush,
    /// which is what lets <c>TenantBaselineSeeder</c> keep its one-SaveChanges-inside-the-caller's-
    /// transaction contract.</para>
    ///
    /// <para>This lives here rather than in each caller because the seeding rule was previously
    /// restated at every creation path — and one of them, <c>DemoUserSeeder</c>, simply did not
    /// have it, so a demo/pilot business unit was created at boot with no <c>QuoteStatus</c> rows
    /// at all and could not raise a quote.</para>
    /// </summary>
    /// <param name="businessUnit">Tracked or already-saved; its id is what the existence check
    /// reads and its navigation is what the new rows are attached to.</param>
    public static async Task<int> EnsureAsync(
        ErpRfqAutomationContext db, BusinessUnit businessUnit, string actor,
        DateTime? now = null, CancellationToken ct = default)
    {
        // IgnoreQueryFilters: these seeders run on the platform plane or at boot, where the tenant
        // filter is a no-op, but a filtered existence check called from a context scoped to a
        // DIFFERENT business unit would answer "absent" for rows that exist and write duplicates.
        var present = (await db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                // An inactive lifecycle row cannot satisfy a hard runtime dependency. Treat it as
                // absent so an explicit repair creates a new active governed row without mutating
                // the tenant's historical reference data.
                .Where(row => row.BusinessUnitId == businessUnit.Id && row.IsActive != false)
                .Select(row => new { row.SetupType, row.SetupCode })
                .ToListAsync(ct))
            .Select(row => Key(row.SetupType, row.SetupCode))
            .ToHashSet(StringComparer.Ordinal);

        var missing = CreateFor(businessUnit, actor, now)
            .Where(status => !present.Contains(Key(status.SetupType, status.SetupCode)))
            .ToArray();

        db.SetupMasters.AddRange(missing);
        return missing.Length;
    }

    private static string Key(string? setupType, string? setupCode)
        => string.Concat(
            (setupType ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant(),
            // ASCII unit separator: a delimiter no SetupType or SetupCode can contain, so
            // ("Quote", "StatusDRAFT") and ("QuoteStatus", "DRAFT") cannot collide on one key.
            "\u001f",
            (setupCode ?? string.Empty).ToUpperInvariant());

    public static async Task<long> ResolveIdAsync(
        ErpRfqAutomationContext db, long businessUnitId, string aggregateType, string code, CancellationToken ct = default)
    {
        var setupType = aggregateType switch
        {
            "Lead" => "leadstatus",
            "Rfq" => "rfqstatus",
            "Quote" => "quotestatus",
            "Order" => "orderstatus",
            _ => throw new ArgumentOutOfRangeException(nameof(aggregateType), "Unsupported lifecycle aggregate type.")
        };
        var statuses = await db.SetupMasters.AsNoTracking()
            .Where(item => item.BusinessUnitId == businessUnitId && item.IsActive != false)
            .Where(item => item.SetupType.ToLower().Replace(" ", "") == setupType)
            .Select(item => new { item.SetupId, item.SetupCode, item.SetupValue })
            .ToListAsync(ct);
        var match = statuses
            .Where(item => LifecyclePolicy.Canonicalize(aggregateType, item.SetupCode, item.SetupValue) == code)
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.SetupCode))
            .ThenBy(item => item.SetupId)
            .FirstOrDefault();
        return match?.SetupId
            ?? throw new InvalidOperationException($"{code} is not configured and active for this tenant.");
    }

    private static SetupMaster Create(
        string type, (string Code, string Label) item, BusinessUnit businessUnit, string actor, DateTime createdOn)
        => new()
        {
            SetupType = type,
            SetupCode = item.Code,
            SetupValue = item.Label,
            Description = $"Governed lifecycle state ({LifecyclePolicy.Version})",
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = createdOn,
            BusinessUnit = businessUnit
        };
}
