# Lead Decision Brief — Wiring Notes

The intelligence a sales executive needs to decide **Bid / Review / Skip** on a lead:
catalog coverage, estimated value, margin potential, customer history, deadline
feasibility, and a transparent recommendation in plain language. All code lives in
`Intelligence/Decision/` (namespace `ERP_RFQ_Automation.Intelligence.Decision`) plus the
new `Controllers/LeadDecisionController.cs`. **No existing file was modified, no
packages, no entities, no migrations.**

## Files

| File | What |
|---|---|
| `Intelligence/Decision/DecisionModels.cs` | Wire-contract DTOs (LeadDecisionBrief, CatalogCoverage, CoverageItem, CustomerHistory, DeadlineFeasibility, LeadDecisionSummary) |
| `Intelligence/Decision/ILeadDecisionService.cs` | Service interface (`GetBriefAsync`, `GetSummariesAsync`) |
| `Intelligence/Decision/LeadDecisionService.cs` | Matching, value/margin math, customer resolution, deadline bands, recommendation rules |
| `Intelligence/Decision/DecisionServiceCollectionExtensions.cs` | `AddLeadDecisionIntelligence()` |
| `Intelligence/Decision/LeadDecisionBriefTool.cs` | Copilot tool `lead_decision_brief` (read-only) |
| `Controllers/LeadDecisionController.cs` | REST endpoints |

## Program.cs splice (lead applies)

```csharp
using ERP_RFQ_Automation.Intelligence.Decision;   // top of Program.cs

builder.Services.AddLeadDecisionIntelligence();    // anywhere among the service registrations
```

`AddLeadDecisionIntelligence()` registers only `ILeadDecisionService` (scoped). It
deliberately does NOT register the agent tool — the tool set is owned by
`Agent/AgentServiceCollectionExtensions.cs`.

## Agent tool registration (lead splices into `AgentServiceCollectionExtensions.AddAgentEngine`)

```csharp
using ERP_RFQ_Automation.Intelligence.Decision;   // top of AgentServiceCollectionExtensions.cs

// ---- Lead decision intelligence tool ----
services.AddScoped<IAgentTool, LeadDecisionBriefTool>();   // "lead_decision_brief" (read-only)
```

Requires `AddLeadDecisionIntelligence()` to also be registered (the tool depends on
`ILeadDecisionService`). `IsMutation = false`, so no guardrail case is needed.

## Endpoints (BU from `businessUnitId` JWT claim, SEC-07 convention)

- `GET  /api/intelligence/leads/{id}/decision-brief` → full `LeadDecisionBrief` (camelCase)
- `POST /api/intelligence/leads/decision-summaries` body `{ "leadIds": [1, 2, …] }`
  (cap 100 → 400 above that) → `{ "summaries": { "<leadId>": LeadDecisionSummary } }`

404 = lead not in the caller's BU. Summaries silently omit unknown / foreign-tenant
ids — a list view never errors because one row is stale. Missing data (no items, no
prices, no customer, no deadline) never throws anywhere; the brief just reports it.

## Recommendation rules (transparent, always explained)

| Rule (evaluated top-down) | Result |
|---|---|
| `coveragePct < 20` **OR** deadline overdue | `skip` |
| `coveragePct ≥ 60` **AND** not overdue **AND** (existing customer **OR** `marginPotentialPct ≥ 15`) | `bid` |
| everything else | `review` |

`reasons[]` always contains plain-language sentences covering coverage/stock, value,
margin (when computable), customer history, deadline, and a "Low extraction confidence —
verify the extracted lines before quoting." warning when `Lead.Aiconfidence < 0.70`.

## Signal implementation notes

- **Coverage** — batched matching, never per-line queries: one IN query for every
  distinct `ItemMaterialCode`/`ManufacturerPartNumber` vs `Product.PartNo`/`ModelNo`
  (case-insensitive via `ToLower()`), then a **bounded** name-contains fallback for
  still-unmatched lines only: max 10 ILIKE queries (longest ≥4-char token of
  `ProductShortName`/`ProductShortDescription`, LIKE-escaped), max 15 candidates each.
  Match precedence per line: `code` > `mpn` > `name` (surfaced as `matchType`).
  `inStock` = matched product's `QtyOnHand > 0`.
- **Estimated value** — per line: the lead's own `UnitPrice` (> 0) else the matched
  product's `FinalSalesPrice ?? SellingPrice`, × `Quantity` (qty ≤ 0 contributes 0).
  `valueConfidence` = `"high"` only when **more than half** of the lines got a real
  price; else `"low"`. `currency` = the single distinct `LeadItem.Currency` across
  lines; mixed or absent is reported honestly as `null` (no FX invented).
- **Margin potential** — avg `(price − cost) / price` over lines that are matched AND
  priced, with `cost = FinalLandedCost ?? UnitCost`; expressed as a percentage
  (0–100, 1 dp). `null` when no line has both numbers — never guessed.
- **Customer history** — buyer email = `Lead.Clientemail ?? EmailIngest.FromEmail`
  (EmailIngest carries only sender metadata: `FromEmail`/`ToEmail`/subject; the
  address is extracted from `"Name <addr>"` forms). Customer resolved by escaped
  ILIKE name equality or `ContactEmail` equality (email hit wins), scoped
  `Buid == null || Buid == bu` like the global filter. `pastLeads` counts same-name
  leads in the BU even when no customer record exists. `quotes` = all quotes to the
  customer; `orders`/`totalOrderValue` = last 24 months (`SUM` via nullable
  projection, so an empty history is 0, not a throw).
- **Deadline** — `BidClosingDate` dates before year 2000 are treated as sentinels →
  `daysLeft = null`, urgency `"unknown"`. Bands: `overdue` (< 0) / `critical` (≤ 3d) /
  `soon` (≤ 7d) / `comfortable`; `workloadHint` is a human string ("1,919 item(s)
  across 3 day(s) (~640 lines/day)."). Unknown deadline is **not** overdue.
- **Summaries (batch)** — exactly two batched queries for the whole request: leads +
  minimal item fields in one, all distinct material codes vs `Product.PartNo` in the
  other. Coverage = exact-code matches only; value = lead-item prices only;
  recommendation is **coarse** (same coverage/overdue gates, `bid` when coverage ≥ 60%
  and not overdue — customer/margin signals are deliberately not consulted at list
  scale). Ids are de-duplicated and capped at 100 in the service as well.
- **Tenant safety** — every query carries an explicit BU predicate
  (`BusinessUnitId == bu` on Leads/Quotes/Orders; `Buid == null || Buid == bu` on
  Products/Customers) on top of the global query filters — same convention as
  `PricingEngine`, so the service stays correct on tenant-less context paths
  (background/agent execution).

## Example JSON — `GET /api/intelligence/leads/4711/decision-brief`

```json
{
  "leadId": 4711,
  "rfqno": "RFQ-2026-0917",
  "buyersName": "Al Futtaim Engineering",
  "extractionConfidence": 0.91,
  "coverage": {
    "totalItems": 40,
    "coveredItems": 34,
    "coveragePct": 85.0,
    "inStockItems": 29,
    "items": [
      {
        "leadItemId": 90210,
        "description": "Hex bolt M12x50 A2-70",
        "quantity": 500,
        "matched": true,
        "matchType": "code",
        "productId": 118,
        "inStock": true,
        "unitPrice": 0.42,
        "priceSource": "lead"
      },
      {
        "leadItemId": 90211,
        "description": "Proprietary gasket XK-9",
        "quantity": 12,
        "matched": false,
        "matchType": null,
        "productId": null,
        "inStock": false,
        "unitPrice": null,
        "priceSource": null
      }
    ]
  },
  "estimatedValue": 12480.50,
  "valueConfidence": "high",
  "currency": "USD",
  "marginPotentialPct": 18.5,
  "customer": {
    "isExistingCustomer": true,
    "customerName": "Al Futtaim Engineering LLC",
    "pastLeads": 6,
    "quotes": 9,
    "orders": 4,
    "totalOrderValue": 210350.00
  },
  "deadline": {
    "bidClosingDate": "2026-07-20T00:00:00",
    "daysLeft": 3,
    "urgency": "critical",
    "workloadHint": "40 item(s) across 3 day(s) (~14 lines/day)."
  },
  "recommendation": "bid",
  "reasons": [
    "We stock 34 of 40 items (85% coverage, 29 on hand).",
    "Estimated value 12,481 USD.",
    "Healthy margin potential (~18.5%) on the items we can cost.",
    "Existing customer (Al Futtaim Engineering LLC) — 210,350 in orders over the last 24 months.",
    "Deadline in 3 day(s) — tight for 40 items."
  ]
}
```

## Example JSON — `POST /api/intelligence/leads/decision-summaries`

Request `{ "leadIds": [4711, 4712] }` →

```json
{
  "summaries": {
    "4711": { "leadId": 4711, "coveragePct": 72.5, "estimatedValue": 11890.00, "daysLeft": 3, "urgency": "critical", "recommendation": "bid" },
    "4712": { "leadId": 4712, "coveragePct": 10.0, "estimatedValue": 0, "daysLeft": null, "urgency": "unknown", "recommendation": "skip" }
  }
}
```

## Copilot tool

- `lead_decision_brief` `{leadId}` — read-only (`IsMutation = false`); compact brief:
  recommendation + reasons, coverage counts, value/currency/margin, customer history,
  deadline band. No per-item rows (kept small for the model context).

## Build status (2026-07-17)

`dotnet build` — full solution build **succeeded (0 errors)**; zero warnings originate
from any `Intelligence/Decision/*` file or `LeadDecisionController.cs` (the 104
pre-existing warnings are all in other engineers' files). Nothing committed.
