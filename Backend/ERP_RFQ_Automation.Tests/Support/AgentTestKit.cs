using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// A minimal, configurable <see cref="IAgentTool"/> for guardrail tests. The guardrail
/// engine must NEVER execute a tool itself, so <see cref="ExecuteAsync"/> throws — if a
/// guardrail test trips it, that's a real bug surfaced as a failure.
/// </summary>
public sealed class FakeAgentTool : IAgentTool
{
    public FakeAgentTool(string name, bool isMutation)
    {
        Name = name;
        IsMutation = isMutation;
    }

    public string Name { get; }
    public string Description => $"Fake tool '{Name}' for tests.";
    public string InputJsonSchema => "{\"type\":\"object\"}";
    public bool IsMutation { get; }

    public Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("The guardrail engine must never execute tools.");
}

/// <summary>
/// Seed helpers for the autonomous-agent guardrail + sourcing entities, following the
/// same conventions as <see cref="Seed"/>: explicit ids, explicit timestamps (SQLite
/// cannot evaluate the Postgres now() column defaults), FK-satisfying parents.
/// </summary>
public static class AgentSeed
{
    public static readonly DateTime Now = new(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Parses a JSON string into a detached <see cref="JsonElement"/> usable
    /// after the backing document is disposed.</summary>
    public static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static AgentPolicy Policy(
        ErpRfqAutomationContext ctx,
        long businessUnitId,
        AgentAutonomyLevel level,
        decimal maxAutoAwardValue = 0m,
        decimal maxAutoOrderValue = 0m,
        bool requireApprovalForAwards = true,
        bool requireApprovalForOrders = true,
        bool requireApprovalForSupplierEmails = true,
        string? perToolOverrides = null,
        long? currencyId = null)
    {
        var policy = new AgentPolicy
        {
            BusinessUnitId = businessUnitId,
            AutonomyLevel = level,
            // Defaults to null — an unconfigured cap currency — because that is what every row
            // that predates the currency column looks like, and tests should meet the fail-closed
            // path unless they deliberately opt out of it.
            CurrencyId = currencyId,
            MaxAutoAwardValue = maxAutoAwardValue,
            MaxAutoOrderValue = maxAutoOrderValue,
            RequireApprovalForAwards = requireApprovalForAwards,
            RequireApprovalForOrders = requireApprovalForOrders,
            RequireApprovalForSupplierEmails = requireApprovalForSupplierEmails,
            PerToolOverrides = perToolOverrides,
            CreatedOn = Now,
            UpdatedOn = Now
        };
        ctx.Set<AgentPolicy>().Add(policy);
        return policy;
    }

    public static Rfq Rfq(ErpRfqAutomationContext ctx, long id, long businessUnitId, string? rfqNo = null)
    {
        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        var rfq = new Rfq
        {
            Id = id,
            Rfqno = rfqNo ?? $"RFQ-{id}",
            BuyersName = "Acme Buyer",
            RecDate = Now,
            BusinessUnitId = businessUnitId,
            CreatedBy = "seed",
            CreatedDate = Now
        };
        ctx.Rfqs.Add(rfq);
        return rfq;
    }

    public static Rfqitem RfqItem(ErpRfqAutomationContext ctx, long id, long rfqId, string productName, int quantity)
    {
        var item = new Rfqitem
        {
            Id = id,
            Rfqid = rfqId,
            ProductShortName = productName,
            Quantity = quantity,
            CreatedBy = "seed",
            CreatedDate = Now
        };
        ctx.Rfqitems.Add(item);
        return item;
    }

    public static Supplier Supplier(ErpRfqAutomationContext ctx, long id, long? buid, string name, string? email = null)
    {
        if (buid.HasValue) Seed.EnsureBusinessUnit(ctx, buid.Value);
        var supplier = new Supplier
        {
            Id = id,
            Name = name,
            ContactEmail = email,
            ImageUrl = "n/a",
            Buid = buid,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = Now
        };
        ctx.Suppliers.Add(supplier);
        return supplier;
    }

    public static SupplierSolicitation Solicitation(
        ErpRfqAutomationContext ctx,
        long id,
        long businessUnitId,
        long rfqId,
        long supplierId,
        SolicitationStatus status = SolicitationStatus.Sent)
    {
        if (!ctx.Rfqs.Local.Any(x => x.Id == rfqId) && !ctx.Rfqs.Any(x => x.Id == rfqId))
            Rfq(ctx, rfqId, businessUnitId);
        if (!ctx.Suppliers.Local.Any(x => x.Id == supplierId) && !ctx.Suppliers.Any(x => x.Id == supplierId))
            Supplier(ctx, supplierId, businessUnitId, $"Supplier {supplierId}");
        var s = new SupplierSolicitation
        {
            Id = id,
            BusinessUnitId = businessUnitId,
            RfqId = rfqId,
            SupplierId = supplierId,
            IdempotencyKey = $"test-solicitation:{businessUnitId}:{id}",
            RequestHash = new string('0', 64),
            Status = status,
            SentOn = Now,
            Channel = "Email",
            CreatedOn = Now,
            UpdatedOn = Now
        };
        ctx.Set<SupplierSolicitation>().Add(s);
        return s;
    }

    public static SourcingAward Award(
        ErpRfqAutomationContext ctx,
        long id,
        long businessUnitId,
        long rfqId,
        long supplierId,
        decimal unitPrice,
        decimal? quantity = null)
    {
        if (!ctx.Rfqs.Local.Any(x => x.Id == rfqId) && !ctx.Rfqs.Any(x => x.Id == rfqId))
            Rfq(ctx, rfqId, businessUnitId);
        if (!ctx.Suppliers.Local.Any(x => x.Id == supplierId) && !ctx.Suppliers.Any(x => x.Id == supplierId))
            Supplier(ctx, supplierId, businessUnitId, $"Supplier {supplierId}");
        var a = new SourcingAward
        {
            Id = id,
            BusinessUnitId = businessUnitId,
            RfqId = rfqId,
            SupplierId = supplierId,
            Status = "APPROVED",
            IdempotencyKey = $"test-award:{businessUnitId}:{id}",
            RequestHash = new string('0', 64),
            UnitPrice = unitPrice,
            Quantity = quantity,
            TotalValue = unitPrice * (quantity ?? 1m),
            CreatedOn = Now
        };
        ctx.Set<SourcingAward>().Add(a);
        return a;
    }
}
