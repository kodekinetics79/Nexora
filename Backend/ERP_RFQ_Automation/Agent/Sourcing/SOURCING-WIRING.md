# Sourcing Loop — Wiring Notes

Closes the previously-facade supplier sourcing loop with real, tenant-scoped,
guardrailed agent tools: **dispatch → capture → compare → award**. All new code lives
under `Agent/Tools/`, `Agent/Sourcing/`, and `Agent/Models/`; entity config is in
`Models/ErpRfqAutomationContext.Agent.cs`. No existing repositories/controllers/models
were modified except the Agent-owned `RecommendAwardTool` (refactored to share scoring).

## New tools

| Tool name | Mutation | Purpose |
|---|---|---|
| `send_rfq_to_suppliers` | ✅ | Dispatch an RFQ to many suppliers at once; records a tracked `SupplierSolicitation` (Sent) per supplier and emails each via `INotificationService.SendRfqToSupplierAsync`. Returns a per-recipient summary. |
| `list_solicitations` | — | Read: who an RFQ was solicited to and each solicitation's status. |
| `capture_supplier_quote` | ✅ | Persist a supplier's returned quote as `SupplierQuotedItem` rows and mark the matching solicitation `Responded`. Lets a demo close the loop without inbound email parsing. |
| `compare_supplier_quotes` | — | Read: per-line comparison matrix across suppliers with a shared multi-criteria score (price 50% / lead time 25% / success rate 25%); best per line + overall recommendation. |
| `award_rfq` | ✅ | Record the award decision as `SourcingAward` rows. Carries the award total for the value cap. |

`dispatch_rfq_to_supplier` (single-supplier) is **kept** — `send_rfq_to_suppliers` is the
multi-supplier, tracked superset. Both remain registered.

Shared scoring was factored out of `RecommendAwardTool` into
`Agent/Sourcing/SupplierScoring.cs` (`IScoreCandidate` + `SupplierScoring.ScoreInPlace`).
`RecommendAwardTool` and `CompareSupplierQuotesTool` both consume it — one formula, no
duplication.

## New entities + migration

- `Agent/Models/SourcingEntities.cs`:
  - `SupplierSolicitation` (Id, BusinessUnitId, RfqId, SupplierId, `SolicitationStatus`
    {Sent, Responded, Declined, Expired}, SentOn, RespondedOn?, Channel, Notes, CreatedOn, UpdatedOn)
  - `SourcingAward` (Id, BusinessUnitId, RfqId, RfqItemId?, SupplierId, UnitPrice, Quantity?,
    TotalValue, Rationale, AwardedByUserId?, AwardedByAgent, CreatedOn)
- Configured in `ConfigureAgentModel` (same partial as the other Agent entities): enum→string
  conversions, `numeric(18,2)` money, `now()` timestamp defaults, per-tenant global query
  filter (`CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId`), and BU-scoped indexes.
- **Migration:** `AddSourcingLoop` (`20260716142759_AddSourcingLoop`). Creates only
  `SupplierSolicitations` and `SourcingAwards` (+ their indexes). **Not applied** — the lead
  applies it to Neon.

## Guardrail additions (`Agent/Guardrails/AgentGuardrail.cs`)

New constants on `AgentToolNames`: `send_rfq_to_suppliers`, `list_solicitations`,
`capture_supplier_quote`, `compare_supplier_quotes`, `award_rfq`.

New `EvaluateAsync` switch cases (reached only for mutations, at Act level — the autonomy
gate already denies at Observe and requires approval at Suggest, and per-tool overrides still
win outright):

- `send_rfq_to_suppliers` → respects `RequireApprovalForSupplierEmails`; else allow.
- `award_rfq` → respects `RequireApprovalForAwards`; else value-cap check against
  `MaxAutoAwardValue` using the **award total**. Total = explicit `totalValue`/`amount` hint if
  present, else `sum(unitPrice × (quantity ?? 1))` over `awards[]` (`ResolveAwardTotal`).
- `capture_supplier_quote` → data entry: no category flag / no cap; allowed at Act. Approval at
  Suggest and denial at Observe come from the existing autonomy gate; a per-tool override can
  still force approval/deny.
- Unknown mutations still hit the fail-safe `default` (require approval).

Every mutation decision continues to flow through the orchestrator's audit log unchanged.

## Field-mapping notes — `SupplierQuotedItem` (capture_supplier_quote)

The existing `SupplierQuotedItem` model must not be modified, and it has **no** RfqId,
RfqItemId, or lead-time columns. Mapping used:

- Each input `line` → one `SupplierQuotedItem` row.
- `SupplierId` = input `supplierId`; `UnitPrice` = line `unitPrice`.
- `Quantity` = line `quantity`, else the RFQ line's `Rfqitem.Quantity`, else `1`.
- `ItemName`, `UomId`, `CurrencyId` = pulled from the matching `Rfqitem` when available.
- **RFQ linkage is encoded in `QuoteReference`** as `rfq={rfqId};item={rfqItemId};lead={leadTimeDays}`.
  `compare_supplier_quotes` parses this back (via `QuoteRef.Parse`) to group bids per line and to
  recover the supplier-quoted lead time (which has no dedicated column). This is the key
  assumption enabling multi-supplier, per-line comparison.
- `currency` input: accepted only as a **numeric `CurrencyId`**. A currency *code* string (e.g.
  "USD") is NOT persisted, because `SupplierQuotedItem` has only `CurrencyId` (a FK) and no code
  column. Falls back to the RFQ line's `CurrencyId`.
- `QuoteDate` = now; `ValidUntil` = parsed input `validUntil`; `CreatedBy` = acting user name or
  `"agent"`; `CreatedDate` = now; `IsActive` = true; `BusinessUnitId` stamped from context.
- `SupplierQuotedItem` is **not** globally tenant-filtered, so `compare_supplier_quotes` scopes
  its read explicitly with `BusinessUnitId == ctx.BusinessUnitId`.

`award_rfq`: `TotalValue = UnitPrice × (Quantity ?? 1)`. `AwardedByAgent = true` when no acting
user id is on the context (autonomous), and `AwardedByUserId` captures the user when present.

## Migration apply command (for the lead — do not run here)

```bash
dotnet ef database update --project Backend/ERP_RFQ_Automation
# or target this migration explicitly:
dotnet ef database update AddSourcingLoop --project Backend/ERP_RFQ_Automation
```
