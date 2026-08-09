using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Deduplication;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.OrderToCash;

public interface ICustomerAwardApplicationService
{
    Task<QuoteAwardProjection> GetByQuoteAsync(long businessUnitId, long quoteId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClientPurchaseOrderInboxRow>> SearchPurchaseOrdersAsync(long businessUnitId,
        string? search, int limit, CancellationToken cancellationToken = default);
    Task<ClientPurchaseOrderMatchView> GetPurchaseOrderMatchAsync(long businessUnitId,
        long purchaseOrderId, CancellationToken cancellationToken = default);
    Task<QuoteLineMatchProposalView> ProposeQuoteLineMatchesAsync(long businessUnitId,
        ProposeQuoteLineMatchCommand command, CancellationToken cancellationToken = default);
    Task<QuoteLineMatchProposalView> ProposePurchaseOrderMatchesAsync(long businessUnitId,
        long purchaseOrderId, long? quoteId = null, CancellationToken cancellationToken = default);
    Task<CustomerPurchaseOrderView> CreatePurchaseOrderAsync(long businessUnitId, string idempotencyKey,
        string correlationId, CreateCustomerPurchaseOrderCommand command, string actor, CancellationToken cancellationToken = default);
    Task<CustomerAwardView> CreateAwardAsync(long businessUnitId, string idempotencyKey,
        string correlationId, CreateCustomerAwardCommand command, string actor, CancellationToken cancellationToken = default);
    Task<CustomerAwardView> ConfirmAwardAsync(long businessUnitId, long awardId, string idempotencyKey,
        string correlationId, VersionedCustomerAwardCommand command, string actor, CancellationToken cancellationToken = default);
    Task<CustomerAwardView> CancelAwardAsync(long businessUnitId, long awardId, string idempotencyKey,
        string correlationId, CancelCustomerAwardCommand command, string actor, CancellationToken cancellationToken = default);
    Task<CustomerAwardOrderView> ConvertToOrderAsync(long businessUnitId, long awardId, string idempotencyKey,
        string correlationId, VersionedCustomerAwardCommand command, string actor, CancellationToken cancellationToken = default);
}

public sealed record CreateCustomerPurchaseOrderCommand(
    long QuoteId,
    long CommercialCaseId,
    long CustomerId,
    long CurrencyId,
    string ExternalPoNumber,
    DateTime PoDate,
    DateTime ReceivedOn,
    long ExpectedVersion,
    IReadOnlyList<CreateCustomerPurchaseOrderLineCommand> Lines);

/// <summary>
/// One line as the BUYER wrote it. Nothing on this record may be defaulted from our own quotation:
/// the discrepancy engine compares this against the quote, so a value copied from the quote makes
/// the comparison self-referential and "no discrepancy" structurally unavoidable.
/// </summary>
public sealed record CreateCustomerPurchaseOrderLineCommand(
    string ExternalLineReference,
    long? ProductId,
    string Description,
    decimal OrderedQuantity,
    int? UomId,
    decimal? UnitPrice,
    decimal? LineAmount,
    string? CustomerItemCode = null,
    string? ManufacturerName = null,
    string? ManufacturerPartNumber = null);

public sealed record CreateCustomerAwardCommand(
    long CustomerPurchaseOrderId,
    long QuoteId,
    long ExpectedVersion,
    long CustomerPurchaseOrderExpectedVersion,
    long QuoteExpectedVersion,
    IReadOnlyList<CreateCustomerAwardAllocationCommand> Allocations);

public sealed record CreateCustomerAwardAllocationCommand(
    long CustomerPurchaseOrderLineId,
    long QuoteItemId,
    decimal AwardedQuantity);

public sealed record VersionedCustomerAwardCommand(long ExpectedVersion);
public sealed record CancelCustomerAwardCommand(long ExpectedVersion, string Reason);

public sealed record CustomerPurchaseOrderLineView(
    long Id,
    long CustomerPurchaseOrderId,
    string ExternalLineReference,
    long? ProductId,
    string Description,
    decimal OrderedQuantity,
    int? UomId,
    decimal? UnitPrice,
    decimal? LineAmount,
    long Version,
    string? CustomerItemCode = null,
    string? ManufacturerName = null,
    string? ManufacturerPartNumber = null);

public sealed record CustomerPurchaseOrderView(
    long Id,
    long CommercialCaseId,
    long CustomerId,
    long CurrencyId,
    string InternalNumber,
    string ExternalPoNumber,
    DateTime PoDate,
    DateTime ReceivedOn,
    string Status,
    long Version,
    IReadOnlyList<CustomerPurchaseOrderLineView> Lines);

public sealed record CustomerAwardAllocationView(
    long Id,
    long CustomerAwardId,
    long CustomerPurchaseOrderLineId,
    long QuoteItemId,
    decimal AwardedQuantity,
    decimal UnitPriceSnapshot,
    decimal DiscountSnapshot,
    decimal TaxSnapshot,
    decimal TotalSnapshot,
    long Version);

public sealed record CustomerAwardView(
    long Id,
    string AwardNumber,
    long CustomerPurchaseOrderId,
    long QuoteId,
    long CommercialCaseId,
    long CustomerId,
    long CurrencyId,
    string Status,
    long Version,
    DateTime? ConfirmedOn,
    IReadOnlyList<CustomerAwardAllocationView> Allocations);

public sealed record CustomerAwardOrderView(long Id, string OrderNo, long CustomerAwardId, string Status, long Version);

public sealed record QuoteAwardBalanceLineView(
    long QuoteItemId,
    long? ProductId,
    string? ProductName,
    string Description,
    decimal QuotedQuantity,
    decimal ConfirmedAwardQuantity,
    decimal RemainingQuantity,
    int? UomId,
    string? UomCode,
    decimal UnitPrice);

public sealed record QuoteAwardProjection(
    long QuoteId,
    string QuoteNo,
    long QuoteVersion,
    string Outcome,
    decimal QuotedQuantity,
    decimal ConfirmedAwardQuantity,
    decimal RemainingQuantity,
    IReadOnlyList<QuoteAwardBalanceLineView> Lines,
    IReadOnlyList<CustomerAwardView> Awards);

public sealed record ClientPurchaseOrderInboxRow(
    long Id,
    string InternalNumber,
    string ExternalPoNumber,
    string CustomerName,
    string NexoraSerial,
    DateTime ReceivedOn,
    string Status,
    long? QuoteId,
    string? QuoteNumber,
    string MatchOutcome,
    int DiscrepancyCount,
    long? CustomerOrderId,
    string? CustomerOrderNumber);

public sealed record ClientPurchaseOrderMatchLineView(
    long CustomerPurchaseOrderLineId,
    string ExternalLineReference,
    string PurchaseOrderDescription,
    decimal OrderedQuantity,
    decimal? PurchaseOrderUnitPrice,
    long? QuoteItemId,
    string? QuoteDescription,
    decimal? QuotedQuantity,
    decimal? QuotedUnitPrice,
    decimal? AcceptedQuantity,
    string MatchStatus,
    IReadOnlyList<string> Differences,
    string? CustomerItemCode = null,
    string? ManufacturerName = null,
    string? ManufacturerPartNumber = null);

/// <summary>
/// FR-COM-02. A request to match buyer lines against one candidate quotation. The lines may be a
/// stored purchase order's, or an operator's in-progress entry that has not been saved yet — the
/// matcher is the same either way, so a reviewer sees the same proposal before and after saving.
/// </summary>
public sealed record ProposeQuoteLineMatchCommand(
    long QuoteId,
    long CustomerId,
    IReadOnlyList<ProposeQuoteLineMatchLineCommand> Lines);

public sealed record ProposeQuoteLineMatchLineCommand(
    string ExternalLineReference,
    string? Description,
    string? CustomerItemCode,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    long? CustomerPurchaseOrderLineId = null);

public sealed record QuoteLineMatchProposalView(
    long QuoteId,
    string QuoteNo,
    long? CustomerId,
    int ProposedCount,
    int ReviewCount,
    IReadOnlyList<PurchaseOrderLineMatchProposal> Lines);

public sealed record ClientPurchaseOrderMatchView(
    ClientPurchaseOrderInboxRow Header,
    long CustomerId,
    long CurrencyId,
    string CurrencyCode,
    DateTime PoDate,
    long Version,
    long? AwardId,
    string? AwardNumber,
    string? AwardStatus,
    IReadOnlyList<ClientPurchaseOrderMatchLineView> Lines);

public sealed class CustomerAwardConflictException(string message) : InvalidOperationException(message);

public sealed class CustomerAwardApplicationService(ErpRfqAutomationContext db) : ICustomerAwardApplicationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ConsumingAwardStatuses = [CustomerAwardStatuses.Confirmed, CustomerAwardStatuses.Ordered];
    private readonly ErpRfqAutomationContext _db = db;

    public async Task<QuoteAwardProjection> GetByQuoteAsync(long businessUnitId, long quoteId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var quote = await _db.Quotes
            .AsNoTracking()
            .Include(x => x.QuoteItems).ThenInclude(x => x.Product)
            .Include(x => x.QuoteItems).ThenInclude(x => x.Rfqitem).ThenInclude(x => x!.Uom)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == quoteId, cancellationToken)
            ?? throw new KeyNotFoundException("Quote was not found in this tenant.");

        var awards = await _db.CustomerAwards
            .AsNoTracking()
            .Include(x => x.LineAllocations)
            .Where(x => x.BusinessUnitId == businessUnitId && x.QuoteId == quoteId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var consumed = await ConfirmedQuoteQuantitiesAsync(businessUnitId, quoteId, null, cancellationToken);
        var lines = quote.QuoteItems.OrderBy(x => x.Id).Select(item =>
        {
            var confirmed = consumed.GetValueOrDefault(item.Id);
            return new QuoteAwardBalanceLineView(
                item.Id,
                item.ProductId,
                item.Product?.ProductName,
                item.ItemDescription ?? item.Product?.Description ?? item.Product?.ProductName ?? $"Quote line {item.Id}",
                item.Quantity,
                confirmed,
                Math.Max(0m, item.Quantity - confirmed),
                item.Rfqitem?.UomId ?? item.Product?.UomId,
                item.Rfqitem?.Uom?.UomCode,
                item.UnitPrice);
        }).ToList();

        var quoted = lines.Sum(x => x.QuotedQuantity);
        var confirmedTotal = lines.Sum(x => x.ConfirmedAwardQuantity);
        var remaining = lines.Sum(x => x.RemainingQuantity);
        var outcome = confirmedTotal == 0m ? "UNAWARDED" : remaining == 0m ? "AWARDED" : "PARTIALLY_AWARDED";
        return new QuoteAwardProjection(quote.Id, quote.QuoteNo, quote.RevisionNo, outcome, quoted, confirmedTotal,
            remaining, lines, awards.Select(MapAward).ToList());
    }

    public async Task<IReadOnlyList<ClientPurchaseOrderInboxRow>> SearchPurchaseOrdersAsync(long businessUnitId,
        string? search, int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        if (limit is < 1 or > 200) throw new ArgumentException("Limit must be between 1 and 200.");
        var term = search?.Trim();
        var query = _db.CustomerPurchaseOrders.AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.CommercialCase)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations).ThenInclude(x => x.Award)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations).ThenInclude(x => x.QuoteItem)
                .ThenInclude(x => x.Rfqitem)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations).ThenInclude(x => x.QuoteItem)
                .ThenInclude(x => x.Product)
            .Include(x => x.Awards).ThenInclude(x => x.Quote)
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (!string.IsNullOrWhiteSpace(term))
        {
            var normalized = term.ToUpperInvariant();
            query = query.Where(x => x.ExternalPoNumber.ToUpper().Contains(normalized)
                || x.InternalNumber.ToUpper().Contains(normalized)
                || x.Customer.Name.ToUpper().Contains(normalized)
                || x.CommercialCase.MasterReference.ToUpper().Contains(normalized));
        }

        var purchaseOrders = await query.OrderByDescending(x => x.ReceivedOn).ThenByDescending(x => x.Id)
            .Take(limit).ToListAsync(cancellationToken);
        var awardIds = purchaseOrders.SelectMany(x => x.Awards)
            .Where(x => x.Status != CustomerAwardStatuses.Cancelled).Select(x => x.Id).ToArray();
        var orders = awardIds.Length == 0
            ? []
            : await _db.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                    && x.CustomerAwardId.HasValue && awardIds.Contains(x.CustomerAwardId.Value))
                .Select(x => new { x.Id, x.OrderNo, AwardId = x.CustomerAwardId!.Value })
                .ToListAsync(cancellationToken);
        return purchaseOrders.Select(purchaseOrder =>
        {
            var award = purchaseOrder.Awards.Where(x => x.Status != CustomerAwardStatuses.Cancelled)
                .OrderByDescending(x => x.Id).FirstOrDefault();
            var order = award is null ? null : orders.FirstOrDefault(x => x.AwardId == award.Id);
            var discrepancyCount = purchaseOrder.Lines.Count(line =>
            {
                var allocation = line.AwardAllocations.FirstOrDefault(x =>
                    x.Award.Status != CustomerAwardStatuses.Cancelled);
                return LineDifferences(line, allocation, allocation?.QuoteItem).Count > 0;
            });
            var outcome = award is null ? "POSSIBLE_MATCH_REVIEW"
                : purchaseOrder.Status == CustomerPurchaseOrderStatuses.PartiallyAwarded ? "PARTIAL_AWARD"
                : discrepancyCount > 0 ? "ACCEPTED_WITH_DIFFERENCES" : "EXACT_ACCEPTANCE";
            return new ClientPurchaseOrderInboxRow(purchaseOrder.Id, purchaseOrder.InternalNumber,
                purchaseOrder.ExternalPoNumber, purchaseOrder.Customer.Name,
                purchaseOrder.CommercialCase.MasterReference, purchaseOrder.ReceivedOn, purchaseOrder.Status,
                award?.QuoteId, award?.Quote.QuoteNo, outcome, discrepancyCount,
                order?.Id, order?.OrderNo);
        }).ToList();
    }

    public async Task<ClientPurchaseOrderMatchView> GetPurchaseOrderMatchAsync(long businessUnitId,
        long purchaseOrderId, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var purchaseOrder = await _db.CustomerPurchaseOrders.AsNoTracking()
            .Include(x => x.Customer).Include(x => x.Currency).Include(x => x.CommercialCase)
            .Include(x => x.Lines).ThenInclude(x => x.Product)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations)
                .ThenInclude(x => x.QuoteItem).ThenInclude(x => x.Rfqitem)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations)
                .ThenInclude(x => x.QuoteItem).ThenInclude(x => x.Product)
            .Include(x => x.Lines).ThenInclude(x => x.AwardAllocations)
                .ThenInclude(x => x.Award).ThenInclude(x => x.Quote)
            .Include(x => x.Awards).ThenInclude(x => x.Quote)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == purchaseOrderId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Client PO was not found in this tenant.");
        var award = purchaseOrder.Awards.Where(x => x.Status != CustomerAwardStatuses.Cancelled)
            .OrderByDescending(x => x.Id).FirstOrDefault();
        var order = award is null ? null : await _db.Orders.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && x.CustomerAwardId == award.Id)
            .Select(x => new { x.Id, x.OrderNo }).SingleOrDefaultAsync(cancellationToken);
        var lines = purchaseOrder.Lines.OrderBy(x => x.Id).Select(line =>
        {
            var allocation = line.AwardAllocations.Where(x => x.Award.Status != CustomerAwardStatuses.Cancelled)
                .OrderByDescending(x => x.Id).FirstOrDefault();
            var quoteLine = allocation?.QuoteItem;
            var differences = LineDifferences(line, allocation, quoteLine);
            var status = allocation is null ? "REVIEW_REQUIRED"
                : differences.Count == 0 ? "EXACT_MATCH"
                : differences.Count == 1 && differences[0] == "QUANTITY_DISCREPANCY" ? "PARTIAL_MATCH"
                : "DISCREPANCY";
            return new ClientPurchaseOrderMatchLineView(line.Id, line.ExternalLineReference,
                line.Description, line.OrderedQuantity, line.UnitPrice, quoteLine?.Id,
                quoteLine?.ItemDescription, quoteLine?.Quantity, quoteLine?.UnitPrice,
                allocation?.AwardedQuantity, status, differences,
                line.CustomerItemCode, line.ManufacturerName, line.ManufacturerPartNumber);
        }).ToList();
        var discrepancyCount = lines.Count(x => x.Differences.Count > 0);
        var outcome = award is null ? "POSSIBLE_MATCH_REVIEW"
            : purchaseOrder.Status == CustomerPurchaseOrderStatuses.PartiallyAwarded ? "PARTIAL_AWARD"
            : discrepancyCount > 0 ? "ACCEPTED_WITH_DIFFERENCES" : "EXACT_ACCEPTANCE";
        var header = new ClientPurchaseOrderInboxRow(purchaseOrder.Id, purchaseOrder.InternalNumber,
            purchaseOrder.ExternalPoNumber, purchaseOrder.Customer.Name,
            purchaseOrder.CommercialCase.MasterReference, purchaseOrder.ReceivedOn, purchaseOrder.Status,
            award?.QuoteId, award?.Quote.QuoteNo, outcome, discrepancyCount, order?.Id, order?.OrderNo);
        return new ClientPurchaseOrderMatchView(header, purchaseOrder.CustomerId, purchaseOrder.CurrencyId,
            purchaseOrder.Currency.Code, purchaseOrder.PoDate, purchaseOrder.Version, award?.Id,
            award?.AwardNumber, award?.Status, lines);
    }

    /// <summary>
    /// FR-COM-02. Propose a quote line for each buyer line using item code, manufacturer and part
    /// number. Read-only by construction: it opens no transaction, takes no idempotency key and
    /// writes nothing, because a proposal is not a decision. Committing the pairing remains
    /// <see cref="CreateAwardAsync"/>, which still requires the allocations as an explicit input.
    /// </summary>
    public async Task<QuoteLineMatchProposalView> ProposeQuoteLineMatchesAsync(long businessUnitId,
        ProposeQuoteLineMatchCommand command, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        ArgumentNullException.ThrowIfNull(command);
        if (command.QuoteId <= 0) throw new ArgumentException("A quotation is required to match against.");
        if (command.Lines is null || command.Lines.Count == 0)
            throw new ArgumentException("At least one customer PO line is required to match.");
        if (command.Lines.Any(x => string.IsNullOrWhiteSpace(x.ExternalLineReference)))
            throw new ArgumentException("Every customer PO line requires a reference.");
        if (command.Lines.Count > 500) throw new ArgumentException("Too many customer PO lines to match at once.");

        return await BuildProposalsAsync(businessUnitId, command.QuoteId, command.CustomerId,
            command.Lines.Select(line => new PurchaseOrderLineKeys(line.ExternalLineReference.Trim(),
                line.Description, line.CustomerItemCode, line.ManufacturerName, line.ManufacturerPartNumber,
                line.CustomerPurchaseOrderLineId)).ToList(), cancellationToken);
    }

    /// <summary>The same proposal for a purchase order that is already stored.</summary>
    public async Task<QuoteLineMatchProposalView> ProposePurchaseOrderMatchesAsync(long businessUnitId,
        long purchaseOrderId, long? quoteId = null, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var purchaseOrder = await _db.CustomerPurchaseOrders.AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Awards)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == purchaseOrderId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Client PO was not found in this tenant.");
        var resolvedQuoteId = quoteId
            ?? purchaseOrder.QuoteId
            ?? purchaseOrder.Awards.Where(x => x.Status != CustomerAwardStatuses.Cancelled)
                .OrderByDescending(x => x.Id).Select(x => (long?)x.QuoteId).FirstOrDefault()
            ?? throw new ArgumentException(
                "This customer PO is not linked to a quotation, so there is nothing to match it against.");

        return await BuildProposalsAsync(businessUnitId, resolvedQuoteId, purchaseOrder.CustomerId,
            purchaseOrder.Lines.OrderBy(x => x.Id).Select(line => new PurchaseOrderLineKeys(
                line.ExternalLineReference, line.Description, line.CustomerItemCode, line.ManufacturerName,
                line.ManufacturerPartNumber, line.Id)).ToList(), cancellationToken);
    }

    /// <summary>
    /// Loads the quotation's own three keys and runs <see cref="CustomerPurchaseOrderLineMatcher"/>.
    ///
    /// <para>The quotation is fetched under the caller's tenant, so a quote belonging to another
    /// business unit is simply not found, and its customer is asserted against the purchase order's,
    /// so one customer's PO can never be matched to another customer's quotation.</para>
    /// </summary>
    private async Task<QuoteLineMatchProposalView> BuildProposalsAsync(long businessUnitId, long quoteId,
        long customerId, IReadOnlyList<PurchaseOrderLineKeys> lines, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.AsNoTracking()
            .Include(x => x.QuoteItems).ThenInclude(x => x.Product)
            .Include(x => x.QuoteItems).ThenInclude(x => x.Rfqitem)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == quoteId, cancellationToken)
            ?? throw new KeyNotFoundException("Quote was not found in this tenant.");
        if (customerId > 0 && quote.CustomerId != customerId)
            throw new ArgumentException("The quotation belongs to a different customer than the purchase order.");

        var consumed = await ConfirmedQuoteQuantitiesAsync(businessUnitId, quoteId, null, cancellationToken);
        var quoteLines = quote.QuoteItems.OrderBy(x => x.Id).Select(item => new QuoteLineKeys(
            item.Id,
            item.ItemDescription ?? item.Product?.Description ?? item.Product?.ProductName ?? $"Quote line {item.Id}",
            item.Rfqitem?.ItemMaterialCode,
            item.Rfqitem?.ManufacturerName,
            item.Rfqitem?.ManufacturerPartNumber,
            [item.Rfqitem?.AlternatePartNumber, item.Product?.PartNo, item.Product?.ModelNo],
            item.Quantity,
            Math.Max(0m, item.Quantity - consumed.GetValueOrDefault(item.Id)),
            item.UnitPrice)).ToList();

        var proposals = CustomerPurchaseOrderLineMatcher.Propose(lines, quoteLines);
        return new QuoteLineMatchProposalView(quote.Id, quote.QuoteNo, quote.CustomerId,
            proposals.Count(x => x.Status == QuoteLineMatchStatuses.Proposed),
            proposals.Count(x => x.Status != QuoteLineMatchStatuses.Proposed),
            proposals);
    }

    /// <summary>
    /// FR-COM-04. Every way the buyer's line differs from what we quoted.
    ///
    /// <para>The comparison is only meaningful while the purchase-order side is captured from the
    /// buyer's document. When those fields are defaulted from our own quote line the engine compares
    /// the system against itself and "no discrepancy" becomes structurally unavoidable, which is why
    /// nothing in the capture path may seed them.</para>
    /// </summary>
    private static List<string> LineDifferences(CustomerPurchaseOrderLine line,
        CustomerAwardLineAllocation? allocation, QuoteItem? quoteLine)
    {
        var differences = new List<string>();
        if (allocation is null) differences.Add("UNQUOTED_OR_UNMATCHED_LINE");
        if (allocation is not null && allocation.AwardedQuantity != line.OrderedQuantity)
            differences.Add("QUANTITY_DISCREPANCY");
        if (quoteLine is not null && allocation is not null && allocation.AwardedQuantity < quoteLine.Quantity)
            differences.Add("PARTIAL_QUOTE_AWARD");
        if (quoteLine is not null && PartIdentityConflicts(line, quoteLine))
            differences.Add("PART_DISCREPANCY");
        if (!line.UnitPrice.HasValue) differences.Add("PO_PRICE_NOT_PROVIDED");
        else if (quoteLine is not null && line.UnitPrice.Value != quoteLine.UnitPrice)
            differences.Add("PRICE_DISCREPANCY");
        return differences;
    }

    /// <summary>
    /// True when the part the buyer ordered is demonstrably not the part we quoted — either a
    /// different catalogue product, or a manufacturer part number that matches none of the part
    /// numbers on the quoted line. Silence on the buyer's side is not a conflict.
    /// </summary>
    private static bool PartIdentityConflicts(CustomerPurchaseOrderLine line, QuoteItem quoteLine)
    {
        if (line.ProductId.HasValue && quoteLine.ProductId != line.ProductId) return true;
        var buyerPart = DuplicateRules.Normalize(line.ManufacturerPartNumber);
        if (buyerPart is null) return false;
        var quotedParts = new[]
        {
            quoteLine.Rfqitem?.ManufacturerPartNumber, quoteLine.Rfqitem?.AlternatePartNumber,
            quoteLine.Product?.PartNo, quoteLine.Product?.ModelNo
        }.Select(DuplicateRules.Normalize).Where(part => part is not null).ToList();
        return quotedParts.Count > 0 && !quotedParts.Contains(buyerPart);
    }

    public Task<CustomerPurchaseOrderView> CreatePurchaseOrderAsync(long businessUnitId, string idempotencyKey,
        string correlationId, CreateCustomerPurchaseOrderCommand command, string actor,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async ct =>
        {
            ValidateCommandIdentity(businessUnitId, idempotencyKey, correlationId, actor);
            if (command.ExpectedVersion != 0)
                throw new ArgumentException("expectedVersion must be 0 when creating a customer PO.");
            ValidatePurchaseOrderCommand(command);
            var requestHash = Hash(command);
            await LockTenantAsync(businessUnitId, ct);
            var replay = await ReplayAsync<CustomerPurchaseOrderView>(businessUnitId,
                OrderToCashCommands.CreatePurchaseOrder, idempotencyKey, requestHash, ct);
            if (replay is not null) return replay;

            var quote = await LoadEligibleQuoteAsync(businessUnitId, command.QuoteId, ct);
            ValidateQuoteIdentity(quote, command.CommercialCaseId, command.CustomerId, command.CurrencyId);

            var normalizedPo = ExternalPurchaseOrderNumber.Normalize(command.ExternalPoNumber);
            if (await _db.CustomerPurchaseOrders.AnyAsync(x => x.BusinessUnitId == businessUnitId
                    && x.CustomerId == command.CustomerId && x.NormalizedExternalPoNumber == normalizedPo, ct))
                throw new CustomerAwardConflictException("This customer PO number already exists for the customer.");

            var productIds = command.Lines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToList();
            if (productIds.Count > 0 && await _db.Products.CountAsync(x => productIds.Contains(x.Id)
                    && x.Buid == businessUnitId, ct) != productIds.Count)
                throw new ArgumentException("One or more PO line products do not belong to this tenant.");
            var uomIds = command.Lines.Where(x => x.UomId.HasValue).Select(x => x.UomId!.Value).Distinct().ToList();
            if (uomIds.Count > 0 && await _db.SetUoms.CountAsync(x => uomIds.Contains(x.UomId)
                    && x.BusinessUnitId == businessUnitId, ct) != uomIds.Count)
                throw new ArgumentException("One or more PO line units of measure do not belong to this tenant.");

            var now = DateTime.UtcNow;
            var purchaseOrder = new CustomerPurchaseOrder
            {
                BusinessUnitId = businessUnitId,
                CommercialCaseId = command.CommercialCaseId,
                CustomerId = command.CustomerId,
                CurrencyId = command.CurrencyId,
                // FR-COM-02. What the buyer was answering, recorded on the order itself so the
                // matcher can reach the quotation without going through an award that may not
                // exist yet.
                QuoteId = quote.Id,
                RfqId = quote.Rfqid,
                InternalNumber = await NextDocumentNumberAsync(businessUnitId, OrderToCashDocumentTypes.CustomerPurchaseOrder, now, ct),
                ExternalPoNumber = command.ExternalPoNumber.Trim(),
                NormalizedExternalPoNumber = normalizedPo,
                PoDate = command.PoDate,
                ReceivedOn = command.ReceivedOn,
                Status = CustomerPurchaseOrderStatuses.Confirmed,
                Version = 1,
                CreatedOn = now,
                CreatedBy = actor,
                Lines = command.Lines.Select(line => new CustomerPurchaseOrderLine
                {
                    BusinessUnitId = businessUnitId,
                    ExternalLineReference = line.ExternalLineReference.Trim(),
                    ProductId = line.ProductId,
                    Description = line.Description.Trim(),
                    OrderedQuantity = line.OrderedQuantity,
                    UomId = line.UomId,
                    UnitPrice = line.UnitPrice,
                    LineAmount = line.LineAmount,
                    CustomerItemCode = Trimmed(line.CustomerItemCode),
                    ManufacturerName = Trimmed(line.ManufacturerName),
                    ManufacturerPartNumber = Trimmed(line.ManufacturerPartNumber),
                    Version = 1
                }).ToList()
            };
            _db.CustomerPurchaseOrders.Add(purchaseOrder);
            await _db.SaveChangesAsync(ct);

            var result = MapPurchaseOrder(purchaseOrder);
            await AddAuditAsync(businessUnitId, OrderToCashAggregateTypes.CustomerPurchaseOrder, purchaseOrder.Id,
                purchaseOrder.Version, OrderToCashCommands.CreatePurchaseOrder, null, purchaseOrder.Status,
                actor, null, requestHash, idempotencyKey, correlationId, result, now, ct);
            await _db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public Task<CustomerAwardView> CreateAwardAsync(long businessUnitId, string idempotencyKey,
        string correlationId, CreateCustomerAwardCommand command, string actor,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async ct =>
        {
            ValidateCommandIdentity(businessUnitId, idempotencyKey, correlationId, actor);
            if (command.ExpectedVersion != 0)
                throw new ArgumentException("expectedVersion must be 0 when creating an award.");
            ValidateAwardCommand(command);
            var requestHash = Hash(command);
            await LockTenantAsync(businessUnitId, ct);
            var replay = await ReplayAsync<CustomerAwardView>(businessUnitId, OrderToCashCommands.CreateAward,
                idempotencyKey, requestHash, ct);
            if (replay is not null) return replay;

            var purchaseOrder = await _db.CustomerPurchaseOrders.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == command.CustomerPurchaseOrderId, ct)
                ?? throw new KeyNotFoundException("Customer PO was not found in this tenant.");
            if (purchaseOrder.Version != command.CustomerPurchaseOrderExpectedVersion)
                throw new CustomerAwardConflictException("The customer PO changed. Reload before creating the award.");
            if (purchaseOrder.Status is CustomerPurchaseOrderStatuses.Cancelled or CustomerPurchaseOrderStatuses.Closed)
                throw new CustomerAwardConflictException("Awards cannot be added to a closed or cancelled customer PO.");

            var quote = await LoadEligibleQuoteAsync(businessUnitId, command.QuoteId, ct);
            if (quote.RevisionNo != command.QuoteExpectedVersion)
                throw new CustomerAwardConflictException("The quote revision changed. Reload before creating the award.");
            ValidateQuoteIdentity(quote, purchaseOrder.CommercialCaseId, purchaseOrder.CustomerId, purchaseOrder.CurrencyId);

            var poLines = purchaseOrder.Lines.ToDictionary(x => x.Id);
            var quoteLines = quote.QuoteItems.ToDictionary(x => x.Id);
            foreach (var allocation in command.Allocations)
            {
                if (!poLines.ContainsKey(allocation.CustomerPurchaseOrderLineId))
                    throw new ArgumentException("An allocation references a PO line outside this customer PO.");
                if (!quoteLines.ContainsKey(allocation.QuoteItemId))
                    throw new ArgumentException("An allocation references a line outside this quote.");
            }
            EnsureCommandBounds(command.Allocations, poLines, quoteLines);

            var now = DateTime.UtcNow;
            var award = new CustomerAward
            {
                BusinessUnitId = businessUnitId,
                AwardNumber = await NextDocumentNumberAsync(businessUnitId, OrderToCashDocumentTypes.CustomerAward, now, ct),
                CustomerPurchaseOrderId = purchaseOrder.Id,
                QuoteId = quote.Id,
                CommercialCaseId = purchaseOrder.CommercialCaseId,
                CustomerId = purchaseOrder.CustomerId,
                CurrencyId = purchaseOrder.CurrencyId,
                Status = CustomerAwardStatuses.Draft,
                Version = 1,
                CreatedOn = now,
                CreatedBy = actor,
                LineAllocations = command.Allocations.Select(allocation =>
                {
                    var item = quoteLines[allocation.QuoteItemId];
                    var snapshots = CalculateSnapshots(quote, item, allocation.AwardedQuantity);
                    return new CustomerAwardLineAllocation
                    {
                        BusinessUnitId = businessUnitId,
                        CustomerPurchaseOrderLineId = allocation.CustomerPurchaseOrderLineId,
                        QuoteItemId = allocation.QuoteItemId,
                        AwardedQuantity = allocation.AwardedQuantity,
                        UnitPriceSnapshot = snapshots.UnitPrice,
                        DiscountSnapshot = snapshots.Discount,
                        TaxSnapshot = snapshots.Tax,
                        TotalSnapshot = snapshots.Total,
                        Version = 1
                    };
                }).ToList()
            };
            _db.CustomerAwards.Add(award);
            await _db.SaveChangesAsync(ct);

            var result = MapAward(award);
            await AddAuditAsync(businessUnitId, OrderToCashAggregateTypes.CustomerAward, award.Id, award.Version,
                OrderToCashCommands.CreateAward, null, award.Status, actor, null, requestHash,
                idempotencyKey, correlationId, result, now, ct);
            await _db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public Task<CustomerAwardView> ConfirmAwardAsync(long businessUnitId, long awardId, string idempotencyKey,
        string correlationId, VersionedCustomerAwardCommand command, string actor,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async ct =>
        {
            ValidateCommandIdentity(businessUnitId, idempotencyKey, correlationId, actor);
            var requestHash = Hash(new { awardId, command.ExpectedVersion });
            await LockTenantAsync(businessUnitId, ct);
            var replay = await ReplayAsync<CustomerAwardView>(businessUnitId, OrderToCashCommands.ConfirmAward,
                idempotencyKey, requestHash, ct);
            if (replay is not null) return replay;

            await LockAwardAsync(businessUnitId, awardId, ct);
            var award = await _db.CustomerAwards.Include(x => x.PurchaseOrder).ThenInclude(x => x.Lines)
                .Include(x => x.LineAllocations)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == awardId, ct)
                ?? throw new KeyNotFoundException("Customer award was not found in this tenant.");
            EnsureVersion(award.Version, command.ExpectedVersion, "customer award");
            if (award.Status != CustomerAwardStatuses.Draft)
                throw new CustomerAwardConflictException("Only a draft award can be confirmed.");

            var quote = await LoadEligibleQuoteAsync(businessUnitId, award.QuoteId, ct);
            ValidateQuoteIdentity(quote, award.CommercialCaseId, award.CustomerId, award.CurrencyId);
            var quoteLines = quote.QuoteItems.ToDictionary(x => x.Id);
            var poLines = award.PurchaseOrder.Lines.ToDictionary(x => x.Id);
            var commands = award.LineAllocations.Select(x => new CreateCustomerAwardAllocationCommand(
                x.CustomerPurchaseOrderLineId, x.QuoteItemId, x.AwardedQuantity)).ToList();
            EnsureCommandBounds(commands, poLines, quoteLines);

            var quoteConsumed = await ConfirmedQuoteQuantitiesAsync(businessUnitId, award.QuoteId, award.Id, ct);
            var poConsumed = await ConfirmedPoQuantitiesAsync(businessUnitId, award.CustomerPurchaseOrderId, award.Id, ct);
            foreach (var allocation in award.LineAllocations)
            {
                var quoteItem = quoteLines[allocation.QuoteItemId];
                if (quoteConsumed.GetValueOrDefault(allocation.QuoteItemId) + allocation.AwardedQuantity > quoteItem.Quantity)
                    throw new CustomerAwardConflictException("The award exceeds the remaining quantity on a quote line.");
                var poLine = poLines[allocation.CustomerPurchaseOrderLineId];
                if (poConsumed.GetValueOrDefault(allocation.CustomerPurchaseOrderLineId) + allocation.AwardedQuantity > poLine.OrderedQuantity)
                    throw new CustomerAwardConflictException("The award exceeds the remaining quantity on a customer PO line.");

                var snapshots = CalculateSnapshots(quote, quoteItem, allocation.AwardedQuantity);
                allocation.UnitPriceSnapshot = snapshots.UnitPrice;
                allocation.DiscountSnapshot = snapshots.Discount;
                allocation.TaxSnapshot = snapshots.Tax;
                allocation.TotalSnapshot = snapshots.Total;
                allocation.Version++;
            }

            var currentQuantities = award.LineAllocations
                .GroupBy(x => x.QuoteItemId)
                .ToDictionary(x => x.Key, x => x.Sum(y => y.AwardedQuantity));
            var completesQuote = quoteLines.Values.All(item =>
                quoteConsumed.GetValueOrDefault(item.Id) + currentQuantities.GetValueOrDefault(item.Id) >= item.Quantity);
            if (completesQuote && award.LineAllocations.Count > 0 && quote.TotalAmount.HasValue)
            {
                var priorTotal = await _db.CustomerAwardLineAllocations
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Award.QuoteId == award.QuoteId
                        && x.CustomerAwardId != award.Id && ConsumingAwardStatuses.Contains(x.Award.Status))
                    .SumAsync(x => (decimal?)x.TotalSnapshot, ct) ?? 0m;
                var currentTotal = award.LineAllocations.Sum(x => x.TotalSnapshot);
                var residual = Money(quote.TotalAmount.Value - priorTotal - currentTotal);
                if (residual != 0m)
                {
                    var finalAllocation = award.LineAllocations.OrderBy(x => x.QuoteItemId).ThenBy(x => x.Id).Last();
                    if (finalAllocation.TotalSnapshot + residual < 0m || finalAllocation.DiscountSnapshot - residual < 0m)
                        throw new CustomerAwardConflictException("The quote discount cannot be reconciled across its awards.");
                    finalAllocation.TotalSnapshot = Money(finalAllocation.TotalSnapshot + residual);
                    finalAllocation.DiscountSnapshot = Money(finalAllocation.DiscountSnapshot - residual);
                }
            }

            var now = DateTime.UtcNow;
            var previousState = award.Status;
            award.Status = CustomerAwardStatuses.Confirmed;
            award.Version++;
            award.ConfirmedOn = now;
            award.ConfirmedBy = actor;
            award.ModifiedOn = now;
            award.ModifiedBy = actor;
            await DerivePurchaseOrderStatusAsync(award.PurchaseOrder, award, actor, now, ct);
            await _db.SaveChangesAsync(ct);

            var result = MapAward(award);
            await AddAuditAsync(businessUnitId, OrderToCashAggregateTypes.CustomerAward, award.Id, award.Version,
                OrderToCashCommands.ConfirmAward, previousState, award.Status, actor, null, requestHash,
                idempotencyKey, correlationId, result, now, ct);
            await _db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public Task<CustomerAwardView> CancelAwardAsync(long businessUnitId, long awardId, string idempotencyKey,
        string correlationId, CancelCustomerAwardCommand command, string actor,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async ct =>
        {
            ValidateCommandIdentity(businessUnitId, idempotencyKey, correlationId, actor);
            if (string.IsNullOrWhiteSpace(command.Reason)) throw new ArgumentException("A cancellation reason is required.");
            var requestHash = Hash(new { awardId, command.ExpectedVersion, Reason = command.Reason.Trim() });
            await LockTenantAsync(businessUnitId, ct);
            var replay = await ReplayAsync<CustomerAwardView>(businessUnitId, OrderToCashCommands.CancelAward,
                idempotencyKey, requestHash, ct);
            if (replay is not null) return replay;

            await LockAwardAsync(businessUnitId, awardId, ct);
            var award = await _db.CustomerAwards.Include(x => x.PurchaseOrder).ThenInclude(x => x.Lines)
                .Include(x => x.LineAllocations)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == awardId, ct)
                ?? throw new KeyNotFoundException("Customer award was not found in this tenant.");
            EnsureVersion(award.Version, command.ExpectedVersion, "customer award");
            if (award.Status == CustomerAwardStatuses.Ordered)
                throw new CustomerAwardConflictException("An award already converted to an order cannot be cancelled.");
            if (award.Status == CustomerAwardStatuses.Cancelled)
                throw new CustomerAwardConflictException("The award is already cancelled.");

            var now = DateTime.UtcNow;
            var previousState = award.Status;
            award.Status = CustomerAwardStatuses.Cancelled;
            award.Version++;
            award.CancelledOn = now;
            award.CancelledBy = actor;
            award.CancellationReason = command.Reason.Trim();
            award.ModifiedOn = now;
            award.ModifiedBy = actor;
            await DerivePurchaseOrderStatusAsync(award.PurchaseOrder, award, actor, now, ct);
            await _db.SaveChangesAsync(ct);

            var result = MapAward(award);
            await AddAuditAsync(businessUnitId, OrderToCashAggregateTypes.CustomerAward, award.Id, award.Version,
                OrderToCashCommands.CancelAward, previousState, award.Status, actor, command.Reason.Trim(),
                requestHash, idempotencyKey, correlationId, result, now, ct);
            await _db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    public Task<CustomerAwardOrderView> ConvertToOrderAsync(long businessUnitId, long awardId,
        string idempotencyKey, string correlationId, VersionedCustomerAwardCommand command, string actor,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async ct =>
        {
            ValidateCommandIdentity(businessUnitId, idempotencyKey, correlationId, actor);
            var requestHash = Hash(new { awardId, command.ExpectedVersion });
            await LockTenantAsync(businessUnitId, ct);
            var replay = await ReplayAsync<CustomerAwardOrderView>(businessUnitId,
                OrderToCashCommands.ConvertAwardToOrder, idempotencyKey, requestHash, ct);
            if (replay is not null) return replay;

            await LockAwardAsync(businessUnitId, awardId, ct);
            var award = await _db.CustomerAwards
                .Include(x => x.PurchaseOrder).ThenInclude(x => x.Lines)
                .Include(x => x.Quote).ThenInclude(x => x.Rfq)
                .Include(x => x.LineAllocations).ThenInclude(x => x.QuoteItem).ThenInclude(x => x.Rfqitem)
                .Include(x => x.LineAllocations).ThenInclude(x => x.QuoteItem).ThenInclude(x => x.Product)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == awardId, ct)
                ?? throw new KeyNotFoundException("Customer award was not found in this tenant.");
            EnsureVersion(award.Version, command.ExpectedVersion, "customer award");
            if (award.Status != CustomerAwardStatuses.Confirmed)
                throw new CustomerAwardConflictException("Only a confirmed award can be converted to an order.");
            if (await _db.Orders.AnyAsync(x => x.BusinessUnitId == businessUnitId && x.CustomerAwardId == award.Id, ct))
                throw new CustomerAwardConflictException("This award has already been converted to an order.");
            if (award.LineAllocations.Any(x => !x.QuoteItem.ProductId.HasValue || x.QuoteItem.ProductId <= 0))
                throw new CustomerAwardConflictException("Every awarded quote line must have a product before order conversion.");

            var draftStatus = await ResolveSetupAsync(businessUnitId, "OrderStatus", "DRAFT", ct)
                ?? throw new CustomerAwardConflictException("No DRAFT OrderStatus is configured for this tenant.");
            var unpaidStatus = await ResolveSetupAsync(businessUnitId, "PaymentStatus", "UNPAID", ct);
            var now = DateTime.UtcNow;
            var order = new Order
            {
                OrderNo = await NextDocumentNumberAsync(businessUnitId, OrderToCashDocumentTypes.SalesOrder, now, ct),
                QuoteId = award.QuoteId,
                LeadId = award.Quote.Rfq?.LeadId,
                Rfqid = award.Quote.Rfqid,
                CustomerId = award.CustomerId,
                CommercialCaseId = award.Quote.CommercialCaseId,
                NexoraSerial = award.Quote.NexoraSerial,
                ContactId = award.Quote.ContactId,
                BusinessUnitId = businessUnitId,
                StatusId = draftStatus.SetupId,
                PaymentStatusId = unpaidStatus?.SetupId,
                OrderDate = now,
                CurrencyId = award.CurrencyId,
                SourceType = OrderSourceTypes.CustomerAward,
                CustomerAwardId = award.Id,
                CreatedBy = actor,
                CreatedOn = now,
                IsActive = true
            };
            foreach (var allocation in award.LineAllocations.OrderBy(x => x.Id))
            {
                var poLine = award.PurchaseOrder.Lines.Single(x => x.Id == allocation.CustomerPurchaseOrderLineId);
                order.OrderItems.Add(new OrderItem
                {
                    ProductId = allocation.QuoteItem.ProductId!.Value,
                    Description = poLine.Description,
                    Quantity = allocation.AwardedQuantity,
                    UnitPrice = allocation.UnitPriceSnapshot,
                    Discount = allocation.DiscountSnapshot,
                    TaxAmount = allocation.TaxSnapshot,
                    TotalAmount = allocation.TotalSnapshot,
                    // The buyer's own unit when their PO states one, otherwise the unit we quoted in.
                    // The PO line no longer inherits our unit at capture time, so without this
                    // fallback an order raised from a PO that omitted the unit would carry none.
                    UomId = poLine.UomId ?? allocation.QuoteItem.Rfqitem?.UomId ?? allocation.QuoteItem.Product?.UomId,
                    CustomerAwardLineAllocationId = allocation.Id,
                    CreatedBy = actor,
                    CreatedDate = now,
                    IsActive = true
                });
            }
            order.SubTotal = Money(order.OrderItems.Sum(x => x.Quantity * x.UnitPrice));
            order.DiscountAmount = Money(order.OrderItems.Sum(x => x.Discount));
            order.TaxAmount = Money(order.OrderItems.Sum(x => x.TaxAmount));
            order.TotalAmount = Money(order.OrderItems.Sum(x => x.TotalAmount));
            order.PaidAmount = 0m;
            order.BalanceAmount = order.TotalAmount;
            _db.Orders.Add(order);

            var previousState = award.Status;
            award.Status = CustomerAwardStatuses.Ordered;
            award.Version++;
            award.ModifiedOn = now;
            award.ModifiedBy = actor;
            await _db.SaveChangesAsync(ct);

            var result = new CustomerAwardOrderView(order.Id, order.OrderNo, award.Id,
                draftStatus.SetupCode ?? draftStatus.SetupValue, 1);
            await AddAuditAsync(businessUnitId, OrderToCashAggregateTypes.CustomerAward, award.Id, award.Version,
                OrderToCashCommands.ConvertAwardToOrder, previousState, award.Status, actor, null,
                requestHash, idempotencyKey, correlationId, result, now, ct);
            await _db.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);

    private async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            // PostgreSQL commands are serialized by the tenant advisory lock below.
            // READ COMMITTED gives a waiter a fresh snapshot after that lock is acquired,
            // which is required for canonical idempotency replay of a concurrent request.
            var isolation = _db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await _db.Database.BeginTransactionAsync(isolation, cancellationToken);
            try
            {
                var result = await action(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<Quote> LoadEligibleQuoteAsync(long businessUnitId, long quoteId, CancellationToken cancellationToken)
    {
        await LockQuoteAsync(businessUnitId, quoteId, cancellationToken);
        var quote = await _db.Quotes
            .Include(x => x.Status)
            .Include(x => x.Rfq).ThenInclude(x => x!.Lead)
            .Include(x => x.QuoteItems)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == quoteId, cancellationToken)
            ?? throw new KeyNotFoundException("Quote was not found in this tenant.");
        if (await _db.Quotes.AnyAsync(x => x.BusinessUnitId == businessUnitId && x.RevisionOfQuoteId == quote.Id, cancellationToken))
            throw new CustomerAwardConflictException("This quote has been superseded. Use the latest quote revision.");
        var status = quote.Status?.SetupCode?.ToUpperInvariant() ?? quote.Status?.SetupValue?.ToUpperInvariant();
        if (status is not ("SENT" or "ACCEPTED" or "ORDERED") && quote.StatusId != 43 && quote.StatusId != 44)
            throw new CustomerAwardConflictException("Only a sent or accepted latest quote revision is eligible for an award.");
        return quote;
    }

    private static void ValidateQuoteIdentity(Quote quote, long commercialCaseId, long customerId, long currencyId)
    {
        if (quote.CustomerId != customerId) throw new ArgumentException("The customer PO customer does not match the quote.");
        if (quote.CurrencyId != currencyId) throw new ArgumentException("The customer PO currency does not match the quote.");
        if (quote.Rfq?.Lead?.CommercialCaseId != commercialCaseId)
            throw new ArgumentException("The commercial case does not match the quote.");
    }

    private static void ValidatePurchaseOrderCommand(CreateCustomerPurchaseOrderCommand command)
    {
        if (command.QuoteId <= 0 || command.CommercialCaseId <= 0 || command.CustomerId <= 0 || command.CurrencyId <= 0)
            throw new ArgumentException("Quote, commercial case, customer, and currency are required.");
        if (string.IsNullOrWhiteSpace(command.ExternalPoNumber)) throw new ArgumentException("Customer PO number is required.");
        if (command.ReceivedOn < command.PoDate) throw new ArgumentException("Received date cannot be earlier than PO date.");
        if (command.Lines is null || command.Lines.Count == 0) throw new ArgumentException("At least one customer PO line is required.");
        if (command.Lines.Any(x => string.IsNullOrWhiteSpace(x.ExternalLineReference)
                || string.IsNullOrWhiteSpace(x.Description) || x.OrderedQuantity <= 0m
                || x.UnitPrice < 0m || x.LineAmount < 0m))
            throw new ArgumentException("Every customer PO line requires a reference, description, and positive quantity with non-negative money.");
        var duplicateReference = command.Lines.GroupBy(x => ExternalPurchaseOrderNumber.Normalize(x.ExternalLineReference))
            .Any(x => x.Count() > 1);
        if (duplicateReference) throw new ArgumentException("Customer PO line references must be unique.");
    }

    private static void ValidateAwardCommand(CreateCustomerAwardCommand command)
    {
        if (command.CustomerPurchaseOrderId <= 0 || command.QuoteId <= 0)
            throw new ArgumentException("Customer PO and quote are required.");
        if (command.CustomerPurchaseOrderExpectedVersion <= 0 || command.QuoteExpectedVersion <= 0)
            throw new ArgumentException("Customer PO and quote expected versions are required.");
        if (command.Allocations is null || command.Allocations.Count == 0)
            throw new ArgumentException("At least one award allocation is required.");
        if (command.Allocations.Any(x => x.CustomerPurchaseOrderLineId <= 0 || x.QuoteItemId <= 0 || x.AwardedQuantity <= 0m))
            throw new ArgumentException("Every allocation requires valid PO and quote lines and a positive quantity.");
        if (command.Allocations.GroupBy(x => new { x.CustomerPurchaseOrderLineId, x.QuoteItemId }).Any(x => x.Count() > 1))
            throw new ArgumentException("Duplicate allocation pairs are not allowed.");
    }

    private static void EnsureCommandBounds(IReadOnlyList<CreateCustomerAwardAllocationCommand> allocations,
        IReadOnlyDictionary<long, CustomerPurchaseOrderLine> poLines, IReadOnlyDictionary<long, QuoteItem> quoteLines)
    {
        foreach (var group in allocations.GroupBy(x => x.CustomerPurchaseOrderLineId))
            if (group.Sum(x => x.AwardedQuantity) > poLines[group.Key].OrderedQuantity)
                throw new ArgumentException("Award quantity exceeds the customer PO line quantity.");
        foreach (var group in allocations.GroupBy(x => x.QuoteItemId))
            if (group.Sum(x => x.AwardedQuantity) > quoteLines[group.Key].Quantity)
                throw new ArgumentException("Award quantity exceeds the quoted quantity.");
    }

    private async Task DerivePurchaseOrderStatusAsync(CustomerPurchaseOrder purchaseOrder, CustomerAward changingAward,
        string actor, DateTime now, CancellationToken cancellationToken)
    {
        var consumed = await ConfirmedPoQuantitiesAsync(purchaseOrder.BusinessUnitId, purchaseOrder.Id, changingAward.Id, cancellationToken);
        if (changingAward.Status is CustomerAwardStatuses.Confirmed or CustomerAwardStatuses.Ordered)
            foreach (var allocation in changingAward.LineAllocations)
                consumed[allocation.CustomerPurchaseOrderLineId] = consumed.GetValueOrDefault(allocation.CustomerPurchaseOrderLineId)
                    + allocation.AwardedQuantity;
        var awarded = consumed.Values.Sum();
        var ordered = purchaseOrder.Lines.Sum(x => x.OrderedQuantity);
        var status = awarded == 0m ? CustomerPurchaseOrderStatuses.Confirmed
            : awarded >= ordered ? CustomerPurchaseOrderStatuses.FullyAwarded
            : CustomerPurchaseOrderStatuses.PartiallyAwarded;
        if (purchaseOrder.Status == status) return;
        purchaseOrder.Status = status;
        purchaseOrder.Version++;
        purchaseOrder.ModifiedOn = now;
        purchaseOrder.ModifiedBy = actor;
    }

    private async Task<Dictionary<long, decimal>> ConfirmedQuoteQuantitiesAsync(long businessUnitId, long quoteId,
        long? excludingAwardId, CancellationToken cancellationToken)
        => await _db.CustomerAwardLineAllocations
            .Where(x => x.BusinessUnitId == businessUnitId && x.Award.QuoteId == quoteId
                && ConsumingAwardStatuses.Contains(x.Award.Status)
                && (!excludingAwardId.HasValue || x.CustomerAwardId != excludingAwardId.Value))
            .GroupBy(x => x.QuoteItemId)
            .Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.AwardedQuantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, cancellationToken);

    private async Task<Dictionary<long, decimal>> ConfirmedPoQuantitiesAsync(long businessUnitId, long purchaseOrderId,
        long? excludingAwardId, CancellationToken cancellationToken)
        => await _db.CustomerAwardLineAllocations
            .Where(x => x.BusinessUnitId == businessUnitId && x.Award.CustomerPurchaseOrderId == purchaseOrderId
                && ConsumingAwardStatuses.Contains(x.Award.Status)
                && (!excludingAwardId.HasValue || x.CustomerAwardId != excludingAwardId.Value))
            .GroupBy(x => x.CustomerPurchaseOrderLineId)
            .Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.AwardedQuantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity, cancellationToken);

    private async Task<string> NextDocumentNumberAsync(long businessUnitId, string documentType, DateTime now,
        CancellationToken cancellationToken)
    {
        var year = now.Year;
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"OrderToCashDocumentCounters\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"DocumentType\" = {documentType} AND \"CalendarYear\" = {year} FOR UPDATE",
                cancellationToken);
        var counter = await _db.OrderToCashDocumentCounters.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
            && x.DocumentType == documentType && x.CalendarYear == year, cancellationToken);
        if (counter is null)
        {
            counter = new OrderToCashDocumentCounter
            {
                BusinessUnitId = businessUnitId,
                DocumentType = documentType,
                CalendarYear = year,
                NextNumber = 2
            };
            _db.OrderToCashDocumentCounters.Add(counter);
            return $"{documentType}-{year}-{1:000000}";
        }
        var allocated = counter.NextNumber;
        counter.NextNumber++;
        return $"{documentType}-{year}-{allocated:000000}";
    }

    private async Task LockTenantAsync(long businessUnitId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(73001, {checked((int)businessUnitId)})", cancellationToken);
    }

    private async Task LockQuoteAsync(long businessUnitId, long quoteId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Quotes\" WHERE \"BusinessUnitID\" = {businessUnitId} AND \"ID\" = {quoteId} FOR UPDATE",
                cancellationToken);
    }

    private async Task LockAwardAsync(long businessUnitId, long awardId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"CustomerAwards\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {awardId} FOR UPDATE",
                cancellationToken);
    }

    private async Task<SetupMaster?> ResolveSetupAsync(long businessUnitId, string type, string code,
        CancellationToken cancellationToken)
        => await _db.SetupMasters.OrderByDescending(x => x.BusinessUnitId == businessUnitId)
            .FirstOrDefaultAsync(x => x.SetupType == type && x.SetupCode == code
                && (x.BusinessUnitId == businessUnitId || x.BusinessUnitId == 1), cancellationToken);

    private async Task<T?> ReplayAsync<T>(long businessUnitId, string commandType, string idempotencyKey,
        string requestHash, CancellationToken cancellationToken) where T : class
    {
        var audit = await _db.OrderToCashAuditEvents.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.CommandType == commandType && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (audit is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(audit.RequestHash), Encoding.ASCII.GetBytes(requestHash)))
            throw new CustomerAwardConflictException("The idempotency key was already used with a different request.");
        return JsonSerializer.Deserialize<T>(audit.ResultJson, JsonOptions)
            ?? throw new InvalidOperationException("The saved idempotent result could not be read.");
    }

    private async Task AddAuditAsync<T>(long businessUnitId, string aggregateType, long aggregateId, long aggregateVersion,
        string commandType, string? previousState, string newState, string actor, string? reason,
        string requestHash, string idempotencyKey, string correlationId, T result, DateTime occurredOn,
        CancellationToken cancellationToken)
    {
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT public.nexora_write_otc_audit({businessUnitId}, {aggregateType}, {aggregateId},
                    {aggregateVersion}, {commandType}, {previousState}, {newState}, {actor}, {reason},
                    {requestHash}, {idempotencyKey}, CAST({resultJson} AS jsonb), {correlationId}, {occurredOn})
                """, cancellationToken);
            return;
        }

        _db.OrderToCashAuditEvents.Add(new OrderToCashAuditEvent
        {
            BusinessUnitId = businessUnitId,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            AggregateVersion = aggregateVersion,
            CommandType = commandType,
            PreviousState = previousState,
            NewState = newState,
            Actor = actor,
            Reason = reason,
            RequestHash = requestHash,
            IdempotencyKey = idempotencyKey,
            ResultJson = resultJson,
            CorrelationId = correlationId,
            OccurredOn = occurredOn
        });
    }

    private static CustomerPurchaseOrderView MapPurchaseOrder(CustomerPurchaseOrder purchaseOrder)
        => new(purchaseOrder.Id, purchaseOrder.CommercialCaseId, purchaseOrder.CustomerId, purchaseOrder.CurrencyId,
            purchaseOrder.InternalNumber, purchaseOrder.ExternalPoNumber, purchaseOrder.PoDate, purchaseOrder.ReceivedOn,
            purchaseOrder.Status, purchaseOrder.Version, purchaseOrder.Lines.OrderBy(x => x.Id).Select(x =>
                new CustomerPurchaseOrderLineView(x.Id, purchaseOrder.Id, x.ExternalLineReference, x.ProductId,
                    x.Description, x.OrderedQuantity, x.UomId, x.UnitPrice, x.LineAmount, x.Version,
                    x.CustomerItemCode, x.ManufacturerName, x.ManufacturerPartNumber)).ToList());

    private static CustomerAwardView MapAward(CustomerAward award)
        => new(award.Id, award.AwardNumber, award.CustomerPurchaseOrderId, award.QuoteId, award.CommercialCaseId,
            award.CustomerId, award.CurrencyId, award.Status, award.Version, award.ConfirmedOn,
            award.LineAllocations.OrderBy(x => x.Id).Select(x => new CustomerAwardAllocationView(
                x.Id, award.Id, x.CustomerPurchaseOrderLineId, x.QuoteItemId, x.AwardedQuantity,
                x.UnitPriceSnapshot, x.DiscountSnapshot, x.TaxSnapshot, x.TotalSnapshot, x.Version)).ToList());

    private static (decimal UnitPrice, decimal Discount, decimal Tax, decimal Total) CalculateSnapshots(
        Quote quote, QuoteItem item, decimal quantity)
    {
        if (item.Quantity <= 0m) throw new CustomerAwardConflictException("A quoted quantity must be positive before it can be awarded.");
        var ratio = quantity / item.Quantity;
        var lineTotalSum = quote.QuoteItems.Sum(x => x.TotalAmount);
        var headerDiscount = quote.TotalAmount.HasValue ? Math.Max(0m, lineTotalSum - quote.TotalAmount.Value) : 0m;
        var allocatedHeaderDiscount = lineTotalSum > 0m
            ? Money(headerDiscount * (item.TotalAmount / lineTotalSum) * ratio)
            : 0m;
        return (decimal.Round(item.UnitPrice, 4, MidpointRounding.AwayFromZero),
            Money((item.Discount ?? 0m) * ratio + allocatedHeaderDiscount),
            Money((item.TaxAmount ?? 0m) * ratio),
            Money(item.TotalAmount * ratio - allocatedHeaderDiscount));
    }

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash<T>(T command) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions)))).ToLowerInvariant();

    private static void EnsureVersion(long actual, long expected, string aggregate)
    {
        if (expected <= 0) throw new ArgumentException($"An expected version is required for the {aggregate}.");
        if (actual != expected) throw new CustomerAwardConflictException($"The {aggregate} changed. Reload and try again.");
    }

    private static void ValidateCommandIdentity(long businessUnitId, string idempotencyKey, string correlationId, string actor)
    {
        EnsureTenant(businessUnitId);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128)
            throw new ArgumentException("A valid Idempotency-Key header is required.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 128)
            throw new ArgumentException("A valid X-Correlation-ID header is required.");
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An authenticated actor is required.");
    }

    private static void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new ArgumentException("A valid tenant claim is required.");
    }
}
