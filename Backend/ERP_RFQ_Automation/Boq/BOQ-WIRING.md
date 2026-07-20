# BOQ Engine — Wiring Guide (WP-BOQ)

Service RFQ → Bill of Quantities engine: service-type requests (maintenance
scopes, installation/commissioning, testing, supply-and-install, manpower or
equipment hire, and — vision-pending — SLD drawings) become structured, priced
BOQs. Everything ships in new files; the ONLY existing files touched are listed
in "Existing-file touchpoints" below. **No migration was generated** — the full
table/column list for the lead's migration is in this document.

---

## 1. Program.cs splice lines (lead)

```csharp
// After builder.Services.AddPricingIntelligence(); add:
builder.Services.AddBoqEngine();          // Boq/BoqServiceCollectionExtensions.cs
```

`AddBoqEngine()` registers:

| Service | Implementation | Lifetime |
|---|---|---|
| `IBoqBuilderService` | `BoqBuilderService` | Scoped |
| `IVisionDocumentReader` | `NotConfiguredVisionReader` | Scoped (**TryAddScoped** — see §6) |

Until this line is spliced, `Controllers/BoqController.cs` endpoints fail DI
resolution (same staged-rollout behavior as the other work packages). Nothing
else in the app is affected.

### Copilot tool registration (Agent/AgentServiceCollectionExtensions.cs)

The agent tool set is owned by the Agent extension — same convention as the
pricing tools, so these lines are NOT inside `AddBoqEngine()`. Splice next to
the other `IAgentTool` registrations:

```csharp
services.AddScoped<IAgentTool, ERP_RFQ_Automation.Boq.DraftBoqTool>();  // mutation — rides the unknown-mutation approval fail-safe
services.AddScoped<IAgentTool, ERP_RFQ_Automation.Boq.GetBoqTool>();    // read-only
```

`draft_boq` is `IsMutation = true` and has **no dedicated guardrail case**, so
the orchestrator's unknown-mutation fail-safe queues it for human approval —
intentional for v1. If/when auto-drafting should be allowed, add a
`draft_boq` case to the guardrail engine (no value cap needed: a Draft BOQ has
no external side effects).

Frontend `humanize.ts` entries for both tools are already added (additive).

---

## 2. Migration: tables & columns (lead generates; NO `dotnet ef migrations add` was run)

All five tables are tenant-scoped (`BusinessUnitId bigint NOT NULL` + the
fail-closed global query filter configured in
`Models/ErpRfqAutomationContext.Boq.cs`). Timestamps default to `now()`.

### `BoqDocuments`
| Column | Type | Null | Notes |
|---|---|---|---|
| Id | bigint identity | no | PK |
| BusinessUnitId | bigint | no | tenant scope |
| LeadId | bigint | **yes** | loose reference to Lead.Id — **no FK constraint** (lead lifecycle owned elsewhere) |
| Title | varchar(300) | no | |
| ServiceCategory | varchar(30) | no | electrical\|mechanical\|civil\|maintenance\|manpower\|mixed\|other |
| Status | varchar(20) | no | Draft\|InReview\|Approved |
| OverallConfidence | numeric(5,2) | yes | 0..1 |
| Notes | varchar(4000) | yes | |
| AssumptionsJson | jsonb | yes | JSON array of assumption strings |
| TotalAmount | numeric(18,2) | no | priced lines only (TBD excluded) |
| TbdCount | int | no | items still needing human details |
| CreatedBy | varchar(256) | yes | |
| CreatedOn | timestamp | no | default `now()` |
| UpdatedOn | timestamp | no | default `now()` |
| ApprovedBy | varchar(256) | yes | |
| ApprovedOn | timestamp | yes | |

Indexes: `IX_BoqDocuments_BU_Status (BusinessUnitId, Status)`,
`IX_BoqDocuments_BU_Lead (BusinessUnitId, LeadId)`.

### `BoqSections`
| Column | Type | Null | Notes |
|---|---|---|---|
| Id | bigint identity | no | PK |
| BusinessUnitId | bigint | no | |
| BoqDocumentId | bigint | no | FK → BoqDocuments.Id, **ON DELETE CASCADE** |
| Seq | int | no | |
| Title | varchar(200) | no | |
| TotalAmount | numeric(18,2) | no | |

Index: `IX_BoqSections_Doc_Seq (BoqDocumentId, Seq)`.

### `BoqItems`
| Column | Type | Null | Notes |
|---|---|---|---|
| Id | bigint identity | no | PK |
| BusinessUnitId | bigint | no | |
| BoqSectionId | bigint | no | FK → BoqSections.Id, **ON DELETE CASCADE** |
| Seq | int | no | |
| ItemCode | varchar(64) | yes | |
| Description | varchar(2000) | no | |
| Unit | varchar(20) | no | EA/m/m²/lot/hr/day… |
| Quantity | numeric(18,3) | no | 0 when IsTbd |
| ItemType | varchar(20) | no | Material\|Labor\|Equipment\|Subcontract |
| UnitRate | numeric(18,4) | yes | |
| TotalAmount | numeric(18,2) | yes | null for TBD/unrated lines |
| Source | varchar(20) | no | extracted\|assembly\|manual |
| Confidence | numeric(5,2) | yes | 0..1 |
| IsTbd | boolean | no | under-specified line — needs a human |
| AssemblyCode | varchar(64) | yes | link into BoqAssemblies.Code |
| EvidenceNote | varchar(1000) | yes | "Cable sizes not stated — quantity TBD", … |

Index: `IX_BoqItems_Section_Seq (BoqSectionId, Seq)`.

### `BoqAssemblies`
| Column | Type | Null | Notes |
|---|---|---|---|
| Id | bigint identity | no | PK |
| BusinessUnitId | bigint | no | |
| Code | varchar(64) | no | e.g. "DB-PANEL-250A" |
| Name | varchar(200) | no | |
| Description | varchar(1000) | yes | |
| ServiceCategory | varchar(30) | no | |
| Unit | varchar(20) | no | the unit ONE assembly represents |
| IsStarter | boolean | no | seeded starter library flag |
| CreatedOn | timestamp | no | default `now()` |
| UpdatedOn | timestamp | no | default `now()` |

Index: `UX_BoqAssemblies_BU_Code (BusinessUnitId, Code)` **UNIQUE** — the
idempotent seed and the seed-race backstop rely on it.

### `BoqAssemblyComponents`
| Column | Type | Null | Notes |
|---|---|---|---|
| Id | bigint identity | no | PK |
| BusinessUnitId | bigint | no | |
| BoqAssemblyId | bigint | no | FK → BoqAssemblies.Id, **ON DELETE CASCADE** |
| Seq | int | no | |
| Description | varchar(500) | no | |
| Unit | varchar(20) | no | |
| QtyPer | numeric(18,4) | no | per ONE unit of the parent assembly |
| ItemType | varchar(20) | no | |
| DefaultRate | numeric(18,4) | yes | starter/default rate |

Index: `IX_BoqAssemblyComponents_Assembly_Seq (BoqAssemblyId, Seq)`.

---

## 3. Starter-assembly seeding (no migration data needed)

Seeding is **lazy, per-BU and idempotent** — no seed data in the migration:

* Trigger points: `GET /api/boq/assemblies` and the explode flow
  (`BoqBuilderService.SeedStarterAssembliesAsync`).
* Guard: seeds only when the tenant has **zero** assemblies, so tenant edits
  (including deleting most starters) are never overwritten; the unique
  `(BusinessUnitId, Code)` index backstops concurrent-request races.
* Content: 10 assemblies (`Boq/BoqStarterAssemblies.cs`) — distribution panel,
  cable run per meter (tray+glands+labor), motor installation, pump overhaul,
  lighting point, small-bore piping per meter, scaffold per m³, technician day
  rate, testing per circuit, HVAC split install. All `IsStarter = true`, with a
  "review before quoting" description; rates are placeholder numbers in the
  tenant base currency, meant to be edited.

---

## 4. HTTP surface (Controllers/BoqController.cs)

All `[Authorize]` + `[RequireModulePermission("Quotations", …)]`; BU always
from the JWT `businessUnitId` claim (SEC-07).

| Endpoint | Permission | Notes |
|---|---|---|
| `POST /api/boq/draft` | Create | `{leadId}` or `{title, text, serviceCategory?}` → full tree |
| `GET /api/boq` | View | paged: `page,pageSize,status,search` |
| `GET /api/boq/{id}` | View | full tree |
| `PUT /api/boq/{id}` | Edit | header/sections/items upsert (match by Id, insert on null/0, delete missing — SubmitLeadReviewAsync style); 409 when Approved |
| `POST /api/boq/{id}/approve` | Edit | 409 while any line still needs details (TBD) |
| `GET /api/boq/assemblies` | View | lazily seeds starters |
| `POST /api/boq/items/{id}/explode?code=` | Edit | replace item with assembly components |
| `GET /api/boq/{id}/export.csv` | View | UTF-8-BOM CSV; TBD lines marked, excluded from totals |

---

## 5. Engine behaviors (honesty rules)

* **Quantities are never invented.** The LLM prompt demands `Quantity: null` +
  `Tbd: true` + a reason for under-specified lines; mapping enforces it again
  (null/≤0 → `IsTbd = true`, `Quantity = 0`). TBD lines are excluded from every
  total and tracked in `TbdCount` ("4 items need details" in the UI).
* **Prices are never invented.** The draft schema has no price fields; rates
  come from humans or the tenant's own assembly library.
* LLM failure / rejected output → honest **skeleton BOQ**
  (Supply / Installation / Testing & Commissioning, all-TBD placeholder lines,
  explanatory note), never a fabricated confident draft.
* Approve refuses while `TbdCount > 0`; Approved documents are locked
  (PUT/explode → 409) until status is set back via the update endpoint's
  header.status (Draft/InReview only).
* Totals: line = qty × rate (both known, not TBD); section = Σ lines;
  document = Σ sections; recomputed on every draft/update/explode.

## 6. Vision plug-in path (AnthropicVisionReader later)

Flow today: draft detects drawing files (extension `.png .jpg .jpeg .tif .tiff
.bmp .webp .dwg .dxf .svg .vsd .vsdx` or `image/*` / CAD mime) → calls
`IVisionDocumentReader.ReadAsync` → `NotConfiguredVisionReader` answers
`Success=false, "Drawing detected — connect a vision-capable AI model…"` → the
draft degrades to the TBD skeleton with that note. Lead drafts also check the
lead's `Attachments (ParentType="Lead")` for drawings.

To plug in a real reader later:

1. Implement `IVisionDocumentReader` (e.g. `AnthropicVisionReader`): send the
   file bytes to a vision-capable model, return
   `VisionReadResult.Ok(textualScopeDescription)` — the text then rides the
   existing `DraftServiceBoqAsync` path unchanged.
2. Register it **before** `AddBoqEngine()` in Program.cs:
   ```csharp
   builder.Services.AddScoped<IVisionDocumentReader, AnthropicVisionReader>();
   builder.Services.AddBoqEngine();   // TryAddScoped keeps your registration
   ```
3. Nothing else changes — controller, engine, tools and UI are already wired
   for the success path.

---

## 7. Existing-file touchpoints (everything else is new files)

Backend:
* `Models/ErpRfqAutomationContext.Tenancy.cs` — one delegating call:
  `ConfigureBoqModel(modelBuilder);` (established partial-splice pattern).
* `Services/Interfaces/ILLMService.cs` — additive `DraftServiceBoqAsync` method.
* `Services/OllamaLlmService.cs` — class marked `partial` (implementation lives
  in the new `Services/OllamaLlmService.Boq.cs`).

Frontend (all additive):
* `src/App.tsx` — 2 lazy imports + `/services/boq`, `/services/boq/:id` routes
  (PermissionGuard "Quotations").
* `src/components/layout/Sidebar.tsx` — "Service BOQs" entry next to Quotations.
* `src/pages/Copilot/humanize.ts` — `draft_boq` / `get_boq` entries.

Deliberately NOT touched: `Extraction/`, `Services/EmailService|FolderService|
ManualUploadService`, `Controllers/ExtractionController`, `Program.cs`,
`Agent/AgentServiceCollectionExtensions.cs`, `LeadDetailPage.tsx` (a
"Build BOQ" button belongs in its action bar later — see report).

## 8. Tests

`ERP_RFQ_Automation.Tests/BoqEngineTests.cs` (TestDb / SQLite over the real
model): recalc totals + TBD exclusion, assembly explosion (qty multiplication,
library rates, seq contiguity, TBD/unknown-code refusal), lazy+idempotent seed,
tenant isolation, drawing fallback skeleton, and LLM-draft TBD mapping.
