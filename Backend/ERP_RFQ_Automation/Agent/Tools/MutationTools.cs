using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Agent.Tools;

/// <summary>
/// Emails an RFQ invitation to a supplier via the notifications module. Mutation —
/// routed through the guardrail engine (RequireApprovalForSupplierEmails by default).
/// </summary>
public sealed class DispatchRfqToSupplierTool : IAgentTool
{
    private readonly ErpRfqAutomationContext _db;
    private readonly INotificationService _notifications;

    public DispatchRfqToSupplierTool(ErpRfqAutomationContext db, INotificationService notifications)
    {
        _db = db;
        _notifications = notifications;
    }

    public string Name => AgentToolNames.DispatchRfqToSupplier;
    public string Description =>
        "Send an RFQ invitation email to a supplier so they can submit pricing and lead times.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"rfqId\":{\"type\":\"integer\"}," +
        "\"supplierId\":{\"type\":\"integer\"}," +
        "\"message\":{\"type\":\"string\"}," +
        "\"dueDate\":{\"type\":\"string\"}}," +
        "\"required\":[\"rfqId\",\"supplierId\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var rfqId = input.GetInt64OrNull("rfqId");
        var supplierId = input.GetInt64OrNull("supplierId");
        if (rfqId is null || supplierId is null)
            return AgentToolResult.Fail("rfqId and supplierId are required.");

        var rfq = await _db.Set<Rfq>().AsNoTracking()
            .Where(r => r.Id == rfqId.Value)
            .Select(r => new { r.Id, r.Rfqno, r.BuyersName, r.BidClosingDate, r.NoOfLineItems })
            .FirstOrDefaultAsync(ct);
        if (rfq is null) return AgentToolResult.Fail($"RFQ {rfqId} not found.");

        var supplier = await _db.Set<Supplier>().AsNoTracking()
            .Where(s => s.Id == supplierId.Value)
            .Select(s => new { s.Id, s.Name, s.ContactEmail })
            .FirstOrDefaultAsync(ct);
        if (supplier is null) return AgentToolResult.Fail($"Supplier {supplierId} not found.");
        if (string.IsNullOrWhiteSpace(supplier.ContactEmail))
            return AgentToolResult.Fail($"Supplier '{supplier.Name}' has no contact email on file.");

        var dueDate = input.GetStringOrNull("dueDate")
                      ?? rfq.BidClosingDate?.ToString("yyyy-MM-dd")
                      ?? "As soon as possible";

        var request = new RfqToSupplierNotification
        {
            ToEmail = supplier.ContactEmail!,
            ToName = supplier.Name,
            BusinessUnitId = ctx.BusinessUnitId.ToString(),
            SupplierName = supplier.Name,
            RfqNumber = rfq.Rfqno,
            RfqTitle = rfq.BuyersName ?? rfq.Rfqno,
            ItemSummary = rfq.NoOfLineItems.HasValue ? $"{rfq.NoOfLineItems} line item(s)" : "See RFQ details",
            DueDate = dueDate,
            Message = input.GetStringOrNull("message") ?? "Please submit your best pricing and lead times."
        };

        var sent = await _notifications.SendRfqToSupplierAsync(request, ct);
        return sent
            ? AgentToolResult.Ok(new
            {
                dispatched = true,
                rfqId = rfq.Id,
                rfqNo = rfq.Rfqno,
                supplierId = supplier.Id,
                supplierName = supplier.Name,
                supplierEmail = supplier.ContactEmail
            })
            : AgentToolResult.Fail("The RFQ invitation could not be sent (notification dispatch failed).");
    }
}

/// <summary>
/// Creates an order from an existing quote by delegating to the existing
/// <see cref="IOrderService.CreateOrderFromQuoteAsync"/>. Mutation — routed through
/// the guardrail engine (order value cap + RequireApprovalForOrders by default).
/// The order value the guardrail checks is resolved from the quote total.
/// </summary>
public sealed class CreateOrderFromQuoteTool : IAgentTool
{
    private readonly IOrderService _orders;

    public CreateOrderFromQuoteTool(IOrderService orders) => _orders = orders;

    public string Name => AgentToolNames.CreateOrderFromQuote;
    public string Description =>
        "Create a draft order from an existing quotation. The quote must have a linked customer. " +
        "Subject to the order value cap and approval policy.";
    public string InputJsonSchema =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"quoteId\":{\"type\":\"integer\"}," +
        "\"amount\":{\"type\":\"number\",\"description\":\"Optional order value hint for guardrail checks; defaults to the quote total\"}," +
        "\"notes\":{\"type\":\"string\"}}," +
        "\"required\":[\"quoteId\"]}";
    public bool IsMutation => true;

    public async Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var quoteId = input.GetInt64OrNull("quoteId");
        if (quoteId is null) return AgentToolResult.Fail("quoteId is required.");

        try
        {
            var order = await _orders.CreateOrderFromQuoteAsync(quoteId.Value, ctx.BusinessUnitId);
            return AgentToolResult.Ok(new
            {
                created = true,
                orderId = order.Id,
                orderNo = order.OrderNo,
                quoteId = order.QuoteId,
                totalAmount = order.TotalAmount,
                status = order.Status,
                customer = order.CustomerName
            });
        }
        catch (Exception ex)
        {
            // The service throws plain exceptions for business-rule failures (e.g. no
            // linked customer, missing OrderStatus setup). Surface as a tool failure.
            return AgentToolResult.Fail($"Could not create order from quote {quoteId}: {ex.Message}");
        }
    }
}
