# Pricing Intelligence Engine — Wiring Notes

Multi-signal pricing for RFQ→Quote with per-line rationale + confidence. All code lives
in `Intelligence/Pricing/` (namespace `ERP_RFQ_Automation.Intelligence.Pricing`) plus the
new `Controllers/PricingIntelligenceController.cs`. **No existing file was modified, no
packages, no entities, no migrations.**

## Files

| File | What |
|---|---|
| `Intelligence/Pricing/PricingModels.cs` | Wire contract DTOs (PricePreview, PriceLine, PriceSignal, ApplyPricingRequest/Result) |
| `Intelligence/Pricing/IPricingEngine.cs` | Engine interface |
| `Intelligence/Pricing/PricingEngine.cs` | Signal blending, confidence, rationale, apply |
| `Intelligence/Pricing/PricingServiceCollectionExtensions.cs` | `AddPricingIntelligence()` |
| `Intelligence/Pricing/PricingTools.cs` | Copilot tools `price_rfq` + `apply_rfq_pricing` |
| `Controllers/PricingIntelligenceController.cs` | REST endpoints |

## Program.cs splice (lead applies)

```csharp
using ERP_RFQ_Automation.Intelligence.Pricing;   // top of Program.cs

builder.Services.AddPricingIntelligence();        // anywhere among the service registrations
```

`AddPricingIntelligence()` registers only `IPricingEngine` (scoped). It deliberately does
NOT register the agent tools — the tool set is owned by
`Agent/AgentServiceCollectionExtensions.cs`.

## Agent tool registration (lead splices into `AgentServiceCollectionExtensions.AddAgentEngine`)

```csharp
using ERP_RFQ_Automation.Intelligence.Pricing;   // top of AgentServiceCollectionExtensions.cs

// ---- Pricing intelligence tools ----
services.AddScoped<IAgentTool, PriceRfqTool>();          // "price_rfq" (read-only)
services.AddScoped<IAgentTool, ApplyRfqPricingTool>();   // "apply_rfq_pricing" (mutation)
```

Requires `AddPricingIntelligence()` to also be registered (the tools depend on
`IPricingEngine`).

## Guardrails (no guardrail file was touched)

`apply_rfq_pricing` is a mutation and currently lands in the guardrail `default` branch —
the **unknown-mutation fail-safe already requires human approval**, so it is safe to ship
as-is. When the guardrail owner wants an explicit policy, suggested mapping for the
`EvaluateAsync` switch (plus a `"apply_rfq_pricing"` constant on `AgentToolNames`):

```csharp
case "apply_rfq_pricing":
    // Applying prices determines the value of the quote the tenant will send;
    // treat it like an order-value mutation and cap on the total being applied.
    // Total = sum(lines[].unitPrice × line quantity from DB); when lines are omitted
    // (engine recommendations), the total isn't in the input — require approval.
    var pricingTotal = await ResolveAppliedPricingTotalAsync(input, ct); // helper to add
    if (pricingTotal <= 0)
        return GuardrailDecision.RequireApproval("Applying engine-recommended prices; requires approval.");
    return EvaluateCap(pricingTotal, policy.MaxAutoOrderValue, "pricing");
```

## KEY DISCOVERY — where today's UnitPrice comes from

`Repositories/RfqRepository.cs` → `ApproveAsync` (≈ line 310) generates the Quote with:

```csharp
UnitPrice   = i.UnitPrice ?? 0,                    // i = Rfqitem
TotalAmount = i.Quantity * (i.UnitPrice ?? 0),
```

i.e. **quote prices are copied verbatim from `Rfqitem.UnitPrice` and default to 0** —
today they're only populated if a human typed them (or an uploader set them).
Therefore **`ApplyPricingAsync` writes `Rfqitem.UnitPrice`** (+ `ModifiedBy`/`ModifiedDate`),
so the existing approve → quote flow picks the prices up with zero changes.

## Endpoints (BU from `businessUnitId` JWT claim, SEC-07 convention)

- `GET  /api/intelligence/rfqs/{id}/price-preview` → `PricePreview`
- `POST /api/intelligence/rfqs/{id}/apply-pricing` body `{ lines:[{ rfqItemId, unitPrice }] }` → `{ applied, total }`

`applied` = lines updated; `total` = Σ qty × unitPrice across ALL RFQ lines after apply.
404 = RFQ not in the caller's BU; 400 = unknown line id / non-positive price / empty body.

## Signal implementation notes

Blend = weighted mean of every signal that fires; weight = priority × recency × qty-similarity.

| # | source | data | side | priority weight |
|---|---|---|---|---|
| 1 | `priceList` | `View_SupplierPriceList` (latest supplier list price per product) | cost + margin | 1.00 |
| 2 | `recentQuote` | `QuoteItem.UnitPrice` of this tenant's **accepted** quotes (`Quote.StatusId ∈ {32 legacy, 44 current}`) | sell (no margin) | 0.85 |
| 3 | `supplierQuote` | `SupplierQuotedItem` captured for THIS rfq — linkage parsed from `QuoteReference` `rfq={id};item={id};lead={days}`; best (lowest) cost | cost + margin | 0.70 |
| 4 | `purchaseHistory` | `SupplierPurchaseHistory` — recency/qty-weighted average cost | cost + margin | 0.55 |
| 5 | `productMaster` | `FinalSalesPrice ?? SellingPrice` (sell) else `FinalLandedCost ?? UnitCost` (cost) | sell / cost + margin | 0.40 / 0.35 |

- **⚠ price list view**: `ViewSupplierPriceLists` is mapped in the EF model but **no
  Postgres migration creates `View_SupplierPriceList`** — on Neon the query will throw
  `42P01`. The engine wraps it in try/catch: if the view is absent the signal silently
  doesn't fire (logged at Debug). If the lead creates the view later, the signal starts
  firing with no code change.
- **Margin**: `QuoteConfiguration` is branding-only (logo/colors/T&Cs — no margin column)
  and no other tenant margin setting exists → documented constant
  `PricingEngine.DefaultMarginPct = 0.20m` (20% cost-plus) on all cost-side signals.
- **Recency**: half-life decay, weight halves every 180 days; signals older than 24
  months are excluded at query time; unknown dates get 0.6.
- **Quantity awareness**: history/quote rows are additionally weighted by
  `min(rowQty, lineQty) / max(rowQty, lineQty)`, so evidence at comparable volumes
  dominates (a 10 000-unit historical price barely influences a 10-unit line).
- **Currency**: NO FX conversion (Currency.ExchangeRate exists but no FX code does —
  not invented). Line currency = `Rfqitem.CurrencyId` code → `Rfqitem.Currency` text →
  tenant base currency (`Currency.IsBaseCurrency`). Signals with an explicit different
  currency are **excluded**; signals with no currency stamp are assumed to be tenant
  base currency. `PricePreview.currency` reports the currency priced in.
- **Floor**: cost basis (no margin) of the highest-weighted cost-side signal; if only
  sell-side signals fired, floor = recommended ÷ 1.20 and the rationale flags it as
  derived.
- **Confidence (0–1)**: dominant signal's effective weight + 0.05 per corroborating
  signal + up to 0.15 agreement bonus when candidates cluster within ~25% of the
  recommendation; clamped to [0.05, 0.98]. `needsAttention` when confidence < 0.5 or no
  signal fired (rationale is then an honest "No pricing history found…").
- **Tenant scoping**: RFQ + accepted quotes explicitly filtered on `BusinessUnitId`;
  supplier quotes on `BusinessUnitId == null || == bu`; purchase history / price list /
  products keyed by the product ids taken from the tenant's own RFQ lines (and the EF
  global query filters still apply on top).
- **Query shape**: one query per signal, batched over all product ids (`Contains`),
  capped at 500 rows, `AsNoTracking` — no per-line N+1.

## Copilot tools

- `price_rfq` `{rfqId}` — read-only; compact per-line recommendations + total +
  linesNeedingAttention count.
- `apply_rfq_pricing` `{rfqId, lines?:[{rfqItemId, unitPrice}]}` — mutation; omitted
  `lines` = apply the engine's recommendations (unpriceable lines are skipped and
  reported as `skipped`). `AppliedBy` audit stamp = acting user from `AgentToolContext`.

## Build status (2026-07-17)

`dotnet build` — full solution build **succeeded (0 errors)** including all
`Intelligence/Pricing/*` files and `PricingIntelligenceController.cs`. Nothing committed.
