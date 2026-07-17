# Conversion Intelligence — Wiring

All code lives in `Intelligence/Conversion/` (namespace `ERP_RFQ_Automation.Intelligence.Conversion`)
plus the new `Controllers/ConversionIntelligenceController.cs`. No existing file was modified.
Two splices are needed (both yours):

## 1. Program.cs splice

After the existing `builder.Services.AddAgentEngine(builder.Configuration);` (or anywhere in
service registration):

```csharp
using ERP_RFQ_Automation.Intelligence.Conversion;   // top of file

builder.Services.AddConversionIntelligence();
```

The controller is attribute-routed and picked up by the existing `MapControllers()` — no
endpoint wiring needed.

## 2. Agent-tool registration splice

In `Agent/AgentServiceCollectionExtensions.cs`, inside `AddAgentEngine` next to the other
tool registrations:

```csharp
using ERP_RFQ_Automation.Intelligence.Conversion;   // top of file

// ---- Lead -> RFQ conversion intelligence tools ----
services.AddScoped<IAgentTool, PreviewLeadConversionTool>();
services.AddScoped<IAgentTool, ConvertLeadToRfqTool>();
```

Note: `AddConversionIntelligence()` must also be spliced (step 1) — the tools depend on
`ILeadConversionIntelligence`.

## Guardrail behavior (no change made, none needed)

`convert_lead_to_rfq` (IsMutation = true) is intentionally NOT added to the `AgentGuardrail`
switch: the `default` unknown-mutation fail-safe already returns
`RequireApproval("Unrecognized mutation; requires human approval.")`, which is the correct
posture for creating a commercial document. `preview_lead_conversion` is read-only and
always allowed.

Suggested future case (when you next touch `Agent/Guardrails/AgentGuardrail.cs`):

```csharp
case ConversionToolNames.ConvertLeadToRfq:
    // Data-shaping mutation, no monetary value: no cap. Autonomy gates above
    // already deny at Observe / require approval at Suggest; allow at Act.
    return GuardrailDecision.Allow("Act level; lead conversion (document creation) allowed.");
```

(or `RequireApproval` if you want conversions always human-gated even at Act level).

## HTTP contract (camelCase, app-wide web JSON defaults)

- `GET  /api/intelligence/leads/{id}/conversion-preview` → `ConversionPreview`
- `POST /api/intelligence/leads/{id}/convert` body `ConvertRequest` → `{ "rfqId": 123 }`

```
ConversionPreview = { leadId, header:{ rfqno, buyersName, recDate, bidClosingDate },
  items:[{ leadItemId, sourceText, quantity, unitOfMeasure, normalizedQuantity,
    normalizedUom, matches:[{ productId, productName, materialCode,
    manufacturerPartNumber, score, reason }], bestMatchProductId, confidence,
    needsAttention, attentionReason }], overallConfidence }

ConvertRequest = { items:[{ leadItemId, include, productId, quantity, unitOfMeasure }], notes }
```

BU comes from the `businessUnitId` JWT claim (LeadController pattern); 404 lead-not-found,
409 not-accepted / already-converted, 400 bad per-line choices.

## ConvertAsync vs the legacy `LeadRepository.ConvertLeadToRfqAsync`

The legacy method copies **all** lines unconditionally and Rfqitems carry no LeadItemId,
so post-hoc enrichment could not reliably map created rows back to per-line choices —
the mapping is therefore **replicated, not called**. Semantics preserved 1:1:

- Gates: lead must exist in the BU, `LeadStatusId == 24` (accepted), idempotency (one RFQ
  per lead → 409 with the existing RFQ number).
- `Rfqno` derivation: lead's number, else `RFQ-{id}-{utcnow}`, de-duped with a timestamp suffix.
- Header copy (BuyersName, RecDate, BidClosingDate, AcknowledgmentDate, SubDate,
  HeaderRemarks, OpportunityNo, Rfqtype, DurationAgreement), `LeadId` link,
  `BusinessUnitId` stamped from the authenticated parameter, `RfqstatusId = 34` (Draft),
  `CreatedBy`/`CreatedDate`, per-item field copy, single transaction.

Intentional deltas (the "intelligence"):

| Field | Legacy | Intelligent convert |
|---|---|---|
| `Rfqitem.ProductId` | never set | explicit request choice, else best match when confidence ≥ 0.70 |
| `Rfqitem.UomId` | never set | set when the (corrected) UoM resolves against the tenant's `SetUoms` |
| `Rfqitem.UnitOfMeasure` | raw copy | corrected value if provided, else standardized `UomCode`, else raw |
| `Rfqitem.Quantity` | raw copy | corrected value if provided; raw 0 falls back to qty parsed from text |
| `Rfqitem.Aiconfidence` | copies lead line value | match score of the linked product; lead value when unlinked |
| `Rfq.NoOfLineItems` | `lead.NoOfLineItems ?? count` | count of **included** lines (exclusions make the stored header count wrong otherwise) |
| `Rfq.HeaderRemarks` | raw copy | raw copy + optional `\n[Conversion] {notes}` |
| line selection | all lines | request `include=false` lines are skipped (≥1 line required) |

Lines absent from `request.items` default to included with raw values.

## Resolution + tenant safety notes

- Matching: PartNo == ItemMaterialCode (1.0) > ModelNo/PartNo == ManufacturerPartNumber
  (0.95) > normalized-name equality (0.90) > contains/token-overlap on name+description
  (0.40–0.85). Top 3 per line; `needsAttention` when top score < 0.70 or qty/UoM missing.
  Product has no MPN column, so `ModelNo` is used as the MPN analog (also reported as
  `manufacturerPartNumber` in matches).
- Queries: one `IN` query for all part/model numbers + one bounded `ILIKE` (top-2 tokens,
  `Take(40)`) per distinct line name; scoring in memory. Inactive products excluded.
- Tenant safety: Lead/Rfq/Product all flow through the tenant-filtered context **plus**
  explicit BU predicates (legacy pattern). `SetUoms` has no global filter → explicit
  `BusinessUnitId` predicate. Caller-supplied `leadItemId`s must belong to the lead;
  caller-supplied `productId`s must be visible in the tenant catalog. Empty catalog
  degrades gracefully (empty matches, `needsAttention = true`).
