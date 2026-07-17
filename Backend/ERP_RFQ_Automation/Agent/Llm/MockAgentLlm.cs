using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Agent.Guardrails;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>
/// Deterministic, network-free stand-in for Claude. Selected automatically when
/// <c>Agent:Anthropic:ApiKey</c> is empty so the whole engine is demoable with no key.
///
/// Unlike the original single-hop mock, this one walks realistic MULTI-STEP tool
/// chains ("source RFQ 5" → look up the RFQ → find suppliers → send it to the top
/// three → summarize), exercising reads, mutations and the approval path end to end.
///
/// It is completely stateless: on every invocation it re-derives its position in the
/// chain from the conversation history alone (the tool_use/tool_result blocks that
/// follow the latest human message — see <see cref="MockTurn"/>), inspects the actual
/// tool_result JSON to decide the next call, and caps each turn at
/// <see cref="MaxToolCallsPerTurn"/> tool calls before wrapping up in plain English.
/// </summary>
public sealed class MockAgentLlm : IAgentLlm
{
    /// <summary>Hard cap on tool calls per user turn before the mock summarizes.</summary>
    private const int MaxToolCallsPerTurn = 4;

    // Tools being added in parallel by other engineers. Referenced by their contract
    // names only (never their classes); every use is gated on the advertised tool list.
    private const string PreviewLeadConversion = "preview_lead_conversion";
    private const string ConvertLeadToRfq = "convert_lead_to_rfq";
    private const string PriceRfqTool = "price_rfq";
    private const string ApplyRfqPricing = "apply_rfq_pricing";

    public Task<AgentLlmTurnResult> RunTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        var available = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var turn = MockTurn.Read(history);
        return Task.FromResult(Respond(turn, available));
    }

    // ---------------- intent routing ----------------

    private static AgentLlmTurnResult Respond(MockTurnState turn, HashSet<string> available)
    {
        if (turn.Steps.Count >= MaxToolCallsPerTurn)
            return Text(GenericWrapUp(turn));

        var t = turn.UserText.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t))
            return Text(HelpText());

        var rfqId = FindId(t, "rfq");
        var leadId = FindId(t, "lead");
        var supplierId = FindId(t, "supplier", "vendor");
        var quoteId = FindId(t, "quote", "quotation");
        var anyNum = FindAnyNumber(t);

        var sendish = t.Contains("send") || t.Contains("dispatch") || t.Contains("invite");

        // ---- Multi-step chains (most specific intents first) ----
        if (t.Contains("convert") && t.Contains("lead"))
            return ConvertLeadChain(turn, leadId ?? anyNum, available);

        if (t.Contains("apply") && t.Contains("pric"))
            return ApplyPricingChain(turn, rfqId ?? anyNum, available);

        if ((t.Contains("capture") || t.Contains("record")) && t.Contains("quote"))
            return CaptureQuoteChain(turn, rfqId, supplierId, available);

        if (t.Contains("compare") || (t.Contains("best") && (t.Contains("quote") || t.Contains("supplier"))))
            return CompareQuotesChain(turn, rfqId ?? anyNum, available);

        if (t.Contains("recommend"))
            return RecommendAwardHop(turn, rfqId ?? anyNum, available);

        if (t.Contains("award"))
            return AwardChain(turn, rfqId ?? anyNum, available);

        if (t.Contains("pric") || Regex.IsMatch(t, @"\bquote\s+(the\s+)?rfq", RegexOptions.CultureInvariant))
            return PriceChain(turn, rfqId ?? anyNum, available);

        // Reads that share verbs with the chains below go first.
        if (t.Contains("dashboard") || t.Contains("summary") || t.Contains("overview") || t.Contains("kpi"))
            return DashboardHop(turn, available);

        if (t.Contains("solicit") || (t.Contains("who") && sendish) || t.Contains("responded"))
            return SolicitationsHop(turn, rfqId ?? anyNum, available);

        if (t.Contains("sourc") || t.Contains("go to market") || (sendish && supplierId is null))
            return SourcingChain(turn, rfqId ?? anyNum, available);

        if (sendish && supplierId is not null)
            return DispatchHop(turn, rfqId ?? anyNum, supplierId.Value, available);

        if (t.Contains("order") && (t.Contains("create") || t.Contains("place") || t.Contains("raise") || t.Contains("from quote")))
            return CreateOrderHop(turn, quoteId ?? anyNum, available);

        // ---- Single-hop reads ----
        if (t.Contains("supplier") || t.Contains("vendor"))
            return SearchHop(turn, AgentToolNames.SearchSuppliers, "supplier", "suppliers", available);
        if (t.Contains("lead"))
            return SearchHop(turn, AgentToolNames.SearchLeads, "lead", "leads", available);
        if (t.Contains("quote"))
            return SearchHop(turn, AgentToolNames.SearchQuotes, "quote", "quotes", available);
        if (t.Contains("order"))
            return SearchHop(turn, AgentToolNames.SearchOrders, "order", "orders", available);
        if (t.Contains("rfq"))
        {
            var searchy = t.Contains("search") || t.Contains("find") || t.Contains("list") || t.Contains("all ");
            if (rfqId is not null && !searchy)
                return GetRfqHop(turn, rfqId.Value, available);
            return SearchHop(turn, AgentToolNames.SearchRfqs, "RFQ", "RFQs", available);
        }

        // ---- Fallback ----
        if (turn.Steps.Count > 0)
            return Text(GenericWrapUp(turn));
        if (available.Contains(AgentToolNames.GetDashboardSummary))
            return DashboardHop(turn, available);
        return Text(HelpText());
    }

    // ---------------- sourcing chain: get_rfq -> search_suppliers -> send_rfq_to_suppliers ----------------

    private static AgentLlmTurnResult SourcingChain(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.SendRfqToSuppliers))
            return Text("Sending RFQs out to suppliers isn't switched on in this build yet — I can still look records up for you.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "run sourcing for", "source RFQ 12");

        var steps = turn.Steps;
        switch (steps.Count)
        {
            case 0:
                return Call(0, $"On it — let me pull up RFQ {rfqId} first.",
                    AgentToolNames.GetRfq, new { rfqId });

            case 1:
            {
                var rfq = steps[0];
                if (rfq.IsError || rfq.ResultJson is null)
                    return Text($"I couldn't find RFQ {rfqId} in this workspace — double-check the number and I'll get sourcing underway.");
                var rfqNo = MockJson.Str(rfq.ResultJson, "rfqNo") ?? $"#{rfqId}";
                var lineCount = MockJson.Arr(rfq.ResultJson, "items").Count;
                return Call(1,
                    $"RFQ {rfqNo} is ready — {lineCount} line item(s). Now let me find the strongest suppliers to invite.",
                    AgentToolNames.SearchSuppliers, new { query = "", page = 1, pageSize = 5 });
            }

            case 2:
            {
                // Pick the top 3 supplier ids the search ACTUALLY returned
                // (results are already ranked by success rate).
                var search = steps[1];
                var suppliers = MockJson.Arr(search.ResultJson, "items")
                    .Select(s => (Id: MockJson.Int(s, "Id"), Name: MockJson.Str(s, "Name")))
                    .Where(s => s.Id is not null)
                    .Take(3)
                    .ToList();
                if (search.IsError || suppliers.Count == 0)
                    return Text("I couldn't find any active suppliers to invite. Add a few suppliers with contact emails and I'll put this RFQ out to them.");

                var names = string.Join(", ", suppliers.Select(s => s.Name ?? $"supplier {s.Id}"));
                return Call(2,
                    $"I'll invite the top {suppliers.Count}: {names}.",
                    AgentToolNames.SendRfqToSuppliers,
                    new
                    {
                        rfqId,
                        supplierIds = suppliers.Select(s => s.Id!.Value).ToArray(),
                        message = "Please submit your best pricing and lead times."
                    });
            }

            default:
                return Text(SummarizeSendOut(steps, rfqId.Value));
        }
    }

    private static string SummarizeSendOut(IReadOnlyList<MockStep> steps, long rfqId)
    {
        var send = steps[^1];
        var rfqNo = MockJson.Str(steps[0].ResultJson, "rfqNo") ?? $"#{rfqId}";

        if (send.HeldForApproval)
            return $"Everything is lined up for RFQ {rfqNo}: I picked the strongest suppliers and drafted the invitations, " +
                   "and I've queued the send for your approval — check the Approvals tab. The invitations go out the moment you approve.";
        if (send.DeniedByPolicy)
            return $"I prepared the sourcing for RFQ {rfqNo}, but your current policy doesn't let me contact suppliers — an admin can adjust the agent policy if you'd like me to handle this.";
        if (send.IsError)
            return $"I prepared the sourcing for RFQ {rfqNo} but the invitations didn't go out — the details are above. Want me to try again?";

        var solicited = MockJson.Int(send.ResultJson, "solicited") ?? 0;
        var recipients = MockJson.Arr(send.ResultJson, "recipients")
            .Select(r => MockJson.Str(r, "supplierName"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        var who = recipients.Count > 0 ? $" ({string.Join(", ", recipients)})" : "";
        return $"Done — RFQ {rfqNo} is out to {solicited} supplier(s){who}. " +
               "I'll track their responses; once quotes come in, ask me to compare them and I'll recommend an award.";
    }

    // ---------------- compare chain: compare_supplier_quotes -> summary ----------------

    private static AgentLlmTurnResult CompareQuotesChain(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.CompareSupplierQuotes))
            return Text("Quote comparison isn't switched on in this build yet.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "compare quotes for", "compare quotes for RFQ 5");

        if (turn.Steps.Count == 0)
            return Call(0, $"Let me line up the quotes we've received for RFQ {rfqId}.",
                AgentToolNames.CompareSupplierQuotes, new { rfqId });

        return Text(SummarizeComparison(turn.Steps[^1], rfqId.Value));
    }

    private static string SummarizeComparison(MockStep step, long rfqId)
    {
        if (step.IsError || step.ResultJson is null)
            return $"No supplier quotes have been captured for RFQ {rfqId} yet — want me to ask suppliers for pricing? " +
                   $"Just say \"source RFQ {rfqId}\" and I'll send it out.";

        var res = step.ResultJson;
        var lines = MockJson.Arr(res, "lines");
        var bullets = new List<string>();
        foreach (var line in lines.Take(5))
        {
            var item = MockJson.Str(line, "itemName") ?? $"line {MockJson.Int(line, "rfqItemId")}";
            var bestName = MockJson.Str(line, "bestSupplierName") ?? "n/a";
            var best = BestBid(line);
            var unit = MockJson.Num(best, "UnitPrice");
            var lineTotal = MockJson.Num(best, "lineTotal");
            var priceBit = unit is not null
                ? $" at {Money(unit.Value)}/unit" + (lineTotal is not null ? $" ({Money(lineTotal.Value)} total)" : "")
                : "";
            bullets.Add($"- {item}: {bestName}{priceBit}");
        }

        var rec = MockJson.Str(res, "recommendedSupplierName")
                  ?? $"supplier {MockJson.Int(res, "recommendedSupplierId")}";
        var lineCount = MockJson.Int(res, "lineCount") ?? lines.Count;
        var body = bullets.Count > 0 ? "Best option per line:\n" + string.Join("\n", bullets) + "\n" : "";
        return $"Here's how the quotes for RFQ {rfqId} stack up. {body}" +
               $"Across all {lineCount} line(s), {rec} comes out strongest on price, lead time and track record. " +
               $"Say \"award RFQ {rfqId}\" and I'll record it.";
    }

    // ---------------- award chain: compare_supplier_quotes -> award_rfq ----------------

    private static AgentLlmTurnResult AwardChain(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.AwardRfq))
            return Text("Recording awards isn't switched on in this build yet — I can still compare quotes for you.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "award", "award RFQ 5");

        var steps = turn.Steps;
        switch (steps.Count)
        {
            case 0:
                return Call(0, $"Before I record anything, let me check the quotes on RFQ {rfqId}.",
                    AgentToolNames.CompareSupplierQuotes, new { rfqId });

            case 1:
            {
                var cmp = steps[0];
                if (cmp.IsError || cmp.ResultJson is null)
                    return Text($"I can't award RFQ {rfqId} yet — no supplier quotes have been captured. " +
                                $"Say \"source RFQ {rfqId}\" and I'll invite suppliers to bid first.");

                var (awards, total) = BuildAwardsFromComparison(cmp.ResultJson.Value);
                if (awards.Count == 0)
                    return Text($"The quotes on RFQ {rfqId} don't have enough detail for me to build an award — " +
                                "capture at least one priced line per supplier and I'll take it from there.");

                var rec = MockJson.Str(cmp.ResultJson, "recommendedSupplierName")
                          ?? $"supplier {MockJson.Int(cmp.ResultJson, "recommendedSupplierId")}";
                var rationale = MockJson.Str(cmp.ResultJson, "rationale")
                                ?? "Best weighted score across price, lead time and success rate.";
                return Call(1, $"The numbers favour {rec} — recording the award now.",
                    AgentToolNames.AwardRfq,
                    new { rfqId, awards, rationale, totalValue = total });
            }

            default:
                return Text(SummarizeAward(steps, rfqId.Value));
        }
    }

    private static (List<Dictionary<string, object>> awards, decimal total) BuildAwardsFromComparison(JsonElement cmp)
    {
        var awards = new List<Dictionary<string, object>>();
        decimal total = 0m;
        foreach (var line in MockJson.Arr(cmp, "lines"))
        {
            var best = BestBid(line);
            if (best is null) continue;

            var supplierId = MockJson.Int(best, "SupplierId");
            var unitPrice = MockJson.Num(best, "UnitPrice");
            if (supplierId is null || unitPrice is null) continue;

            var qty = MockJson.Num(best, "Quantity") ?? 1m;
            var award = new Dictionary<string, object>
            {
                ["supplierId"] = supplierId.Value,
                ["unitPrice"] = unitPrice.Value,
                ["quantity"] = qty
            };
            var rfqItemId = MockJson.Int(line, "rfqItemId");
            if (rfqItemId is not null) award["rfqItemId"] = rfqItemId.Value;

            awards.Add(award);
            total += unitPrice.Value * qty;
        }
        return (awards, total);
    }

    private static string SummarizeAward(IReadOnlyList<MockStep> steps, long rfqId)
    {
        var award = steps[^1];
        if (award.HeldForApproval)
        {
            var recap = "";
            if (steps[0].ResultJson is { } cmp)
            {
                var (awards, total) = BuildAwardsFromComparison(cmp);
                if (awards.Count > 0) recap = $" ({awards.Count} line(s), about {Money(total)})";
            }
            return $"I've prepared the award for RFQ {rfqId}{recap} and queued it for your approval — check the Approvals tab. " +
                   "It will be recorded the moment you approve.";
        }
        if (award.DeniedByPolicy)
            return $"Your guardrail policy blocked me from recording this award for RFQ {rfqId} — an admin can adjust the agent policy if you'd like me to handle awards.";
        if (award.IsError)
            return $"I tried to record the award for RFQ {rfqId} but it didn't go through — the details are above.";

        var n = MockJson.Int(award.ResultJson, "awardsRecorded") ?? 0;
        var total2 = MockJson.Num(award.ResultJson, "totalValue") ?? 0m;
        return $"Done — award recorded for RFQ {rfqId}: {n} line(s) totalling {Money(total2)}. " +
               "When you're ready, I can help create the order.";
    }

    /// <summary>The bid flagged isBest on a comparison line, else the top-ranked one.</summary>
    private static JsonElement? BestBid(JsonElement line)
    {
        var bids = MockJson.Arr(line, "bids");
        foreach (var b in bids)
        {
            if (MockJson.Flag(b, "isBest")) return b;
        }
        return bids.Count > 0 ? bids[0] : null;
    }

    // ---------------- lead conversion chain: preview_lead_conversion -> convert_lead_to_rfq ----------------

    private static AgentLlmTurnResult ConvertLeadChain(MockTurnState turn, long? leadId, HashSet<string> available)
    {
        var hasPreview = available.Contains(PreviewLeadConversion);
        var hasConvert = available.Contains(ConvertLeadToRfq);
        if (!hasPreview && !hasConvert)
            return Text("Lead conversion isn't switched on in this build yet — I can still search your leads and RFQs.");
        if (leadId is null)
            return ClarifyLead(turn, available);

        var steps = turn.Steps;
        if (steps.Count == 0)
        {
            return hasPreview
                ? Call(0, $"Let me check what converting lead {leadId} would create first.", PreviewLeadConversion, new { leadId })
                : Call(0, $"Converting lead {leadId} into an RFQ now.", ConvertLeadToRfq, new { leadId });
        }

        if (steps[^1].ToolName == PreviewLeadConversion)
        {
            var preview = steps[^1];
            if (preview.IsError || preview.ResultJson is null)
                return Text($"I couldn't find lead {leadId} — double-check the id and I'll convert it for you.");
            if (!hasConvert)
                return Text($"Lead {leadId} previews cleanly as an RFQ, but the conversion itself isn't switched on in this build yet.");
            return Call(steps.Count, "The preview looks good — converting it now.", ConvertLeadToRfq, new { leadId });
        }

        return Text(SummarizeConversion(steps[^1], leadId.Value));
    }

    private static string SummarizeConversion(MockStep convert, long leadId)
    {
        if (convert.HeldForApproval)
            return $"I've prepared the conversion of lead {leadId} into an RFQ and queued it for your approval — check the Approvals tab.";
        if (convert.DeniedByPolicy)
            return $"Your guardrail policy blocked converting lead {leadId} — an admin can adjust the agent policy if needed.";
        if (convert.IsError)
            return $"The conversion of lead {leadId} didn't go through — the details are above.";

        var newRfqId = MockJson.Int(convert.ResultJson, "rfqId", "newRfqId", "createdRfqId");
        var rfqNo = MockJson.Str(convert.ResultJson, "rfqNo", "rfqNumber");
        if (rfqNo is not null || newRfqId is not null)
        {
            var label = rfqNo ?? $"#{newRfqId}";
            var idForNext = newRfqId?.ToString(CultureInfo.InvariantCulture) ?? rfqNo;
            return $"Done — lead {leadId} is now RFQ {label}. Say \"source RFQ {idForNext}\" and I'll send it to suppliers.";
        }
        return $"Done — lead {leadId} has been converted into an RFQ. Ask me to source it when you're ready.";
    }

    // ---------------- pricing chains: price_rfq [-> apply_rfq_pricing] ----------------

    private static AgentLlmTurnResult PriceChain(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(PriceRfqTool))
            return Text("Pricing isn't switched on in this build yet — I can still compare supplier quotes for you.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "price", "price RFQ 5");

        if (turn.Steps.Count == 0)
            return Call(0, $"Let me work up pricing for RFQ {rfqId}.", PriceRfqTool, new { rfqId });

        return Text(SummarizePricing(turn.Steps[^1], rfqId.Value));
    }

    private static AgentLlmTurnResult ApplyPricingChain(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(ApplyRfqPricing))
            return PriceChain(turn, rfqId, available); // degrade gracefully: at least show recommendations
        if (rfqId is null)
            return ClarifyRfq(turn, available, "apply pricing to", "apply pricing to RFQ 5");

        var steps = turn.Steps;
        switch (steps.Count)
        {
            case 0:
                return available.Contains(PriceRfqTool)
                    ? Call(0, $"Let me price RFQ {rfqId} first so we apply solid numbers.", PriceRfqTool, new { rfqId })
                    : Call(0, $"Applying pricing to RFQ {rfqId} now.", ApplyRfqPricing, new { rfqId });

            case 1 when steps[0].ToolName == PriceRfqTool:
                if (steps[0].IsError || steps[0].ResultJson is null)
                    return Text($"I couldn't price RFQ {rfqId} — it may be missing line items. The details are above.");
                return Call(1, "The numbers look solid — applying them to the RFQ now.", ApplyRfqPricing, new { rfqId });

            default:
                return Text(SummarizeApplyPricing(steps[^1], rfqId.Value));
        }
    }

    private static string SummarizeApplyPricing(MockStep apply, long rfqId)
    {
        if (apply.HeldForApproval)
            return $"The pricing for RFQ {rfqId} is ready, and I've queued the update for your approval — check the Approvals tab.";
        if (apply.DeniedByPolicy)
            return $"Your guardrail policy blocked applying pricing to RFQ {rfqId} — an admin can adjust the agent policy if needed.";
        if (apply.IsError)
            return $"I worked up the pricing but couldn't apply it to RFQ {rfqId} — the details are above.";
        return $"Done — pricing has been applied to RFQ {rfqId}. Line prices and totals are up to date.";
    }

    private static string SummarizePricing(MockStep step, long rfqId)
    {
        if (step.IsError || step.ResultJson is null)
            return $"I couldn't build pricing for RFQ {rfqId} — it may not have any line items yet.";

        var res = step.ResultJson;
        var lines = MockJson.Arr(res, "lines", "items", "recommendations", "linePrices", "prices");
        var bullets = new List<string>();
        foreach (var line in lines.Take(5))
        {
            var name = MockJson.Str(line, "itemName", "product", "name", "description")
                       ?? $"line {MockJson.Int(line, "rfqItemId", "itemId", "lineId", "id")}";
            var price = MockJson.Num(line, "recommendedUnitPrice", "recommendedPrice", "suggestedUnitPrice", "unitPrice", "price");
            bullets.Add(price is not null ? $"- {name}: {Money(price.Value)}" : $"- {name}");
        }
        var total = MockJson.Num(res, "total", "totalValue", "totalAmount", "recommendedTotal", "grandTotal");
        var totalLine = total is not null ? $" Recommended total: {Money(total.Value)}." : "";

        if (bullets.Count == 0)
            return $"I've drafted pricing for RFQ {rfqId}.{totalLine} Say \"apply pricing to RFQ {rfqId}\" and I'll write it onto the RFQ.";
        return $"Here's my recommended pricing for RFQ {rfqId}:\n{string.Join("\n", bullets)}\n" +
               $"{totalLine.TrimStart()} Say \"apply pricing to RFQ {rfqId}\" and I'll write these onto the RFQ.";
    }

    // ---------------- capture chain: get_rfq -> capture_supplier_quote ----------------

    private static AgentLlmTurnResult CaptureQuoteChain(MockTurnState turn, long? rfqId, long? supplierId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.CaptureSupplierQuote))
            return Text("Recording supplier quotes isn't switched on in this build yet.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "record this quote against", "capture supplier 3's quote for RFQ 5");
        if (supplierId is null)
            return Text($"Which supplier sent this quote? For example: \"capture supplier 3's quote for RFQ {rfqId}\".");

        var steps = turn.Steps;
        switch (steps.Count)
        {
            case 0:
                return Call(0, $"Let me pull up RFQ {rfqId}'s line items so the quote lands on the right lines.",
                    AgentToolNames.GetRfq, new { rfqId });

            case 1:
            {
                var rfq = steps[0];
                if (rfq.IsError || rfq.ResultJson is null)
                    return Text($"I couldn't find RFQ {rfqId} — double-check the id and I'll record the quote.");
                var lines = BuildDemoQuoteLines(rfq.ResultJson.Value, supplierId.Value);
                if (lines.Count == 0)
                    return Text($"RFQ {rfqId} has no line items yet, so there's nothing to quote against.");
                return Call(1, $"Recording supplier {supplierId}'s quote across {lines.Count} line(s).",
                    AgentToolNames.CaptureSupplierQuote, new { rfqId, supplierId, lines });
            }

            default:
            {
                var cap = steps[^1];
                if (cap.HeldForApproval)
                    return Text("I've queued that quote entry for your approval — check the Approvals tab.");
                if (cap.DeniedByPolicy)
                    return Text("Your guardrail policy blocked recording that quote — an admin can adjust the agent policy if needed.");
                if (cap.IsError)
                    return Text("I couldn't record that quote — the details are above.");
                var n = MockJson.Int(cap.ResultJson, "linesCaptured") ?? 0;
                return Text($"Done — recorded {n} quoted line(s) from supplier {supplierId} against RFQ {rfqId}. " +
                            $"Once other suppliers respond, say \"compare quotes for RFQ {rfqId}\" and I'll rank them.");
            }
        }
    }

    /// <summary>
    /// Deterministic demo quote lines derived from the RFQ's own items: the RFQ's
    /// unit price when it has one (else a value seeded by the item id), with a small
    /// fixed per-supplier variation so different suppliers produce different bids.
    /// </summary>
    private static List<Dictionary<string, object>> BuildDemoQuoteLines(JsonElement rfq, long supplierId)
    {
        var lines = new List<Dictionary<string, object>>();
        foreach (var item in MockJson.Arr(rfq, "items").Take(5))
        {
            var itemId = MockJson.Int(item, "Id");
            if (itemId is null) continue;

            var basePrice = MockJson.Num(item, "UnitPrice");
            if (basePrice is null || basePrice <= 0m)
                basePrice = 25m + (itemId.Value * 7 % 150);

            var factor = 1m + ((supplierId % 7) - 3m) / 100m; // deterministic +/-3%
            var unitPrice = Math.Round(basePrice.Value * factor, 2);
            var qty = MockJson.Num(item, "Quantity") ?? 1m;
            var lead = (int)(5 + (itemId.Value + supplierId) % 10);

            lines.Add(new Dictionary<string, object>
            {
                ["rfqItemId"] = itemId.Value,
                ["unitPrice"] = unitPrice,
                ["quantity"] = qty,
                ["leadTimeDays"] = lead
            });
        }
        return lines;
    }

    // ---------------- single-hop intents ----------------

    private static AgentLlmTurnResult RecommendAwardHop(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.RecommendAward))
            return AwardChain(turn, rfqId, available);
        if (rfqId is null)
            return ClarifyRfq(turn, available, "recommend an award for", "recommend an award for RFQ 5");

        if (turn.Steps.Count == 0)
            return Call(0, $"Let me weigh up the bids on RFQ {rfqId}.", AgentToolNames.RecommendAward, new { rfqId });

        var step = turn.Steps[^1];
        if (step.IsError || step.ResultJson is null)
            return Text($"I don't have priced supplier bids for RFQ {rfqId} yet — say \"source RFQ {rfqId}\" and I'll invite suppliers to bid.");

        var rec = MockJson.Str(step.ResultJson, "recommendedSupplierName")
                  ?? $"supplier {MockJson.Int(step.ResultJson, "recommendedSupplierId")}";
        var rationale = MockJson.Str(step.ResultJson, "rationale");
        var why = string.IsNullOrWhiteSpace(rationale) ? "" : $" {rationale}";
        return Text($"For RFQ {rfqId}, {rec} is the strongest choice.{why} Say \"award RFQ {rfqId}\" and I'll record it.");
    }

    private static AgentLlmTurnResult DispatchHop(MockTurnState turn, long? rfqId, long supplierId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.DispatchRfqToSupplier))
            return SourcingChain(turn, rfqId, available);
        if (rfqId is null)
            return ClarifyRfq(turn, available, $"send to supplier {supplierId}", $"send RFQ 5 to supplier {supplierId}");

        if (turn.Steps.Count == 0)
            return Call(0, $"Inviting supplier {supplierId} to bid on RFQ {rfqId}.",
                AgentToolNames.DispatchRfqToSupplier,
                new { rfqId, supplierId, message = "Please submit your best pricing and lead times." });

        var step = turn.Steps[^1];
        if (step.HeldForApproval)
            return Text($"I've drafted the invitation to supplier {supplierId} for RFQ {rfqId} and queued it for your approval — check the Approvals tab.");
        if (step.DeniedByPolicy)
            return Text("Your guardrail policy doesn't let me contact suppliers — an admin can adjust the agent policy if needed.");
        if (step.IsError)
            return Text($"The invitation to supplier {supplierId} didn't go out — the details are above.");
        return Text($"Done — supplier {supplierId} has been invited to bid on RFQ {rfqId}. I'll track their response.");
    }

    private static AgentLlmTurnResult CreateOrderHop(MockTurnState turn, long? quoteId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.CreateOrderFromQuote))
            return Text("Creating orders isn't switched on in this build yet.");
        if (quoteId is null)
        {
            if (turn.Steps.Count == 0 && available.Contains(AgentToolNames.SearchQuotes))
                return Call(0, "Let me check which quotes you have on file.",
                    AgentToolNames.SearchQuotes, new { query = "", page = 1, pageSize = 5 });
            var listing = DescribeListing(turn.Steps.Count > 0 ? turn.Steps[^1] : null, "quotes");
            return Text($"{listing}Which quote should I turn into an order? For example: \"create an order from quote 7\".");
        }

        if (turn.Steps.Count == 0)
            return Call(0, $"Creating an order from quote {quoteId}.",
                AgentToolNames.CreateOrderFromQuote,
                new { quoteId, notes = "Created by sourcing copilot" });

        var step = turn.Steps[^1];
        if (step.HeldForApproval)
            return Text($"I've prepared the order from quote {quoteId} and queued it for your approval — check the Approvals tab.");
        if (step.DeniedByPolicy)
            return Text("Your guardrail policy blocked creating that order — an admin can adjust the agent policy if needed.");
        if (step.IsError)
            return Text($"I couldn't create an order from quote {quoteId} — the details are above.");
        var orderNo = MockJson.Str(step.ResultJson, "orderNo");
        return Text(orderNo is not null
            ? $"Done — order {orderNo} has been created from quote {quoteId}."
            : $"Done — the order from quote {quoteId} has been created.");
    }

    private static AgentLlmTurnResult SolicitationsHop(MockTurnState turn, long? rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.ListSolicitations))
            return Text("Solicitation tracking isn't switched on in this build yet.");
        if (rfqId is null)
            return ClarifyRfq(turn, available, "check supplier responses for", "who did we send RFQ 5 to?");

        if (turn.Steps.Count == 0)
            return Call(0, $"Let me check who RFQ {rfqId} went out to.",
                AgentToolNames.ListSolicitations, new { rfqId });

        var step = turn.Steps[^1];
        if (step.IsError || step.ResultJson is null)
            return Text($"I couldn't look up RFQ {rfqId} — double-check the id.");

        var rows = MockJson.Arr(step.ResultJson, "solicitations")
            .Select(s => $"{MockJson.Str(s, "supplierName") ?? $"supplier {MockJson.Int(s, "SupplierId")}"} ({MockJson.Str(s, "status") ?? "Sent"})")
            .ToList();
        if (rows.Count == 0)
            return Text($"RFQ {rfqId} hasn't been sent to any suppliers yet — say \"source RFQ {rfqId}\" and I'll put it out to the market.");
        return Text($"RFQ {rfqId} went out to {rows.Count} supplier(s): {string.Join(", ", rows)}. " +
                    "Once they respond, ask me to compare the quotes.");
    }

    private static AgentLlmTurnResult GetRfqHop(MockTurnState turn, long rfqId, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.GetRfq))
            return SearchHop(turn, AgentToolNames.SearchRfqs, "RFQ", "RFQs", available);

        if (turn.Steps.Count == 0)
            return Call(0, $"Let me pull up RFQ {rfqId}.", AgentToolNames.GetRfq, new { rfqId });

        var step = turn.Steps[^1];
        if (step.IsError || step.ResultJson is null)
            return Text($"I couldn't find RFQ {rfqId} in this workspace — double-check the number.");

        var res = step.ResultJson;
        var rfqNo = MockJson.Str(res, "rfqNo") ?? $"#{rfqId}";
        var buyer = MockJson.Str(res, "buyer");
        var status = MockJson.Str(res, "status");
        var items = MockJson.Arr(res, "items").Count;
        var closing = MockJson.Str(res, "bidClosingDate");
        var parts = new List<string> { $"{items} line item(s)" };
        if (!string.IsNullOrWhiteSpace(buyer)) parts.Add($"buyer {buyer}");
        if (!string.IsNullOrWhiteSpace(status)) parts.Add($"status {status}");
        if (!string.IsNullOrWhiteSpace(closing)) parts.Add($"bids close {closing}");
        return Text($"RFQ {rfqNo}: {string.Join(", ", parts)}. " +
                    $"Say \"source RFQ {rfqId}\" and I'll send it out to suppliers.");
    }

    private static AgentLlmTurnResult DashboardHop(MockTurnState turn, HashSet<string> available)
    {
        if (!available.Contains(AgentToolNames.GetDashboardSummary))
            return Text(HelpText());

        if (turn.Steps.Count == 0)
            return Call(0, "Let me grab the latest numbers.", AgentToolNames.GetDashboardSummary, new { });

        var step = turn.Steps[^1];
        if (step.IsError || step.ResultJson is null)
            return Text("I couldn't load the dashboard just now — try again in a moment.");

        var parts = new List<string>();
        if (step.ResultJson.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in step.ResultJson.Value.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && parts.Count < 6)
                    parts.Add($"{Humanize(p.Name)}: {p.Value}");
            }
        }
        return Text(parts.Count > 0
            ? $"Here's where things stand — {string.Join(", ", parts)}. Want me to dig into any of these?"
            : "Here's your latest snapshot — the full numbers are on the dashboard. Want me to dig into anything specific?");
    }

    private static AgentLlmTurnResult SearchHop(MockTurnState turn, string toolName, string singular, string plural, HashSet<string> available)
    {
        if (!available.Contains(toolName))
            return Text(HelpText());

        if (turn.Steps.Count == 0)
            return Call(0, "Let me look that up.", toolName,
                new { query = ExtractQuery(turn.UserText), page = 1, pageSize = 5 });

        var step = turn.Steps[^1];
        if (step.IsError || step.ResultJson is null)
            return Text("I hit a snag looking that up — mind rephrasing?");

        var total = MockJson.Int(step.ResultJson, "total") ?? 0;
        var items = MockJson.Arr(step.ResultJson, "items");
        if (total == 0 || items.Count == 0)
            return Text($"I didn't find any {plural} matching that. Try different wording, or ask me for an overview of the dashboard.");

        var labels = string.Join("; ", items.Take(5).Select(DescribeRow));
        return Text($"I found {total} {(total == 1 ? singular : plural)}. Most relevant: {labels}. Want details on any of them?");
    }

    // ---------------- clarification helpers (sensible read + short question) ----------------

    private static AgentLlmTurnResult ClarifyRfq(MockTurnState turn, HashSet<string> available, string actionPhrase, string example)
    {
        if (turn.Steps.Count == 0 && available.Contains(AgentToolNames.SearchRfqs))
            return Call(0, "Let me check which RFQs you have open.",
                AgentToolNames.SearchRfqs, new { query = "", page = 1, pageSize = 5 });
        var listing = DescribeListing(turn.Steps.Count > 0 ? turn.Steps[^1] : null, "RFQs");
        return Text($"{listing}Which RFQ should I {actionPhrase}? For example: \"{example}\".");
    }

    private static AgentLlmTurnResult ClarifyLead(MockTurnState turn, HashSet<string> available)
    {
        if (turn.Steps.Count == 0 && available.Contains(AgentToolNames.SearchLeads))
            return Call(0, "Let me check your recent leads.",
                AgentToolNames.SearchLeads, new { query = "", page = 1, pageSize = 5 });
        var listing = DescribeListing(turn.Steps.Count > 0 ? turn.Steps[^1] : null, "leads");
        return Text($"{listing}Which lead should I convert? For example: \"convert lead 12 to an RFQ\".");
    }

    private static string DescribeListing(MockStep? step, string plural)
    {
        if (step?.ResultJson is null || step.IsError) return "";
        var rows = MockJson.Arr(step.ResultJson, "items").Take(5).Select(DescribeRow).ToList();
        return rows.Count == 0 ? "" : $"Here are your most recent {plural}: {string.Join("; ", rows)}. ";
    }

    private static string DescribeRow(JsonElement row)
    {
        var label = MockJson.Str(row, "Name", "rfqNo", "quoteNo", "orderNo") ?? "(unnamed)";
        var id = MockJson.Int(row, "Id");
        var idPart = id is not null ? $" (id {id})" : "";
        var extras = new List<string>();
        var buyer = MockJson.Str(row, "buyer");
        if (!string.IsNullOrWhiteSpace(buyer)) extras.Add(buyer!);
        var amount = MockJson.Num(row, "totalAmount");
        if (amount is not null) extras.Add(Money(amount.Value));
        var status = MockJson.Str(row, "status");
        if (!string.IsNullOrWhiteSpace(status)) extras.Add(status!);
        return extras.Count > 0 ? $"{label}{idPart} — {string.Join(", ", extras)}" : $"{label}{idPart}";
    }

    // ---------------- shared plumbing ----------------

    private static string GenericWrapUp(MockTurnState turn)
    {
        var last = turn.Steps.Count > 0 ? turn.Steps[^1] : null;
        if (last is null) return HelpText();
        if (last.HeldForApproval)
            return "I've queued that action for your approval — check the Approvals tab, and it will run the moment you approve.";
        if (last.DeniedByPolicy)
            return "Your guardrail policy blocked that action — an admin can adjust the agent policy if you'd like me to handle it.";
        if (last.IsError)
            return "I ran into a problem finishing that — the details are above. Want me to try a different approach?";
        return "That's done — the details are above. Anything else you'd like me to take care of?";
    }

    private static string HelpText() =>
        "I can run your whole sourcing loop, even in demo mode: try \"source RFQ 5\", \"compare quotes for RFQ 5\", " +
        "\"award RFQ 5\", \"convert lead 12 to an RFQ\", or \"price RFQ 5\". I can also search RFQs, suppliers, leads, " +
        "quotes and orders, or give you a dashboard overview. Actions that need sign-off are queued in the Approvals tab.";

    private static AgentLlmTurnResult Text(string text) =>
        new() { AssistantText = text, StopReason = AgentTurnStopReason.EndTurn };

    private static AgentLlmTurnResult Call(int stepIndex, string narration, string toolName, object input) =>
        new()
        {
            AssistantText = narration,
            ToolUses = new[]
            {
                new AgentToolUse
                {
                    // Deterministic id — unique within the turn, which is all the
                    // orchestrator needs to pair tool_use with tool_result.
                    Id = $"mocktool_{stepIndex}_{toolName}",
                    Name = toolName,
                    Input = JsonSerializer.SerializeToElement(input)
                }
            },
            StopReason = AgentTurnStopReason.ToolUse
        };

    // ---------------- text parsing ----------------

    /// <summary>Keyword-anchored id extraction: "rfq 5", "RFQ-5", "lead #12", "supplier: 3".</summary>
    private static long? FindId(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
        {
            var m = Regex.Match(text, kw + @"\s*[-#:.]*\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success && long.TryParse(m.Groups[1].Value, out var n) && n > 0) return n;
        }
        return null;
    }

    private static long? FindAnyNumber(string text)
    {
        var m = Regex.Match(text, @"\d+", RegexOptions.CultureInvariant);
        return m.Success && long.TryParse(m.Value, out var n) && n > 0 ? n : null;
    }

    private static string ExtractQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "search","find","show","list","get","me","the","for","all","please","rfq","rfqs",
            "supplier","suppliers","vendor","vendors","lead","leads","quote","quotes","order","orders","a","an","my","our"
        };
        var words = text
            .Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stop.Contains(w))
            .Take(6);
        return string.Join(' ', words);
    }

    private static string Money(decimal d) => d.ToString("#,0.##", CultureInfo.InvariantCulture);

    private static string Humanize(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return propertyName;
        var spaced = Regex.Replace(propertyName, "(?<=[a-z0-9])([A-Z])", " $1", RegexOptions.CultureInvariant);
        return spaced.ToLowerInvariant();
    }
}
