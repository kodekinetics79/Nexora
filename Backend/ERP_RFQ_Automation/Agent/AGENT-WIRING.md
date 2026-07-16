# Agent Engine — Wiring Guide

The autonomous **sourcing copilot** engine (`ERP_RFQ_Automation.Agent`). Claude-powered,
tenant-scoped, guardrailed. Everything new lives under `Agent/` plus a DbSet-free model
config partial (`Models/ErpRfqAutomationContext.Agent.cs`), the `AddAgentEngine` DI
extension, and one migration. This doc lists the exact splices the lead applies.

---

## 1. Program.cs — one line

Add after the notifications registration (`builder.Services.AddNotifications(...)`, ~line 250)
and **before** `var app = builder.Build();`:

```csharp
// Autonomous sourcing copilot (Agent/): Claude tool-use loop + guardrails + audit.
builder.Services.AddAgentEngine(builder.Configuration);
```

`using ERP_RFQ_Automation.Agent;` at the top (or use the fully-qualified call above — the
extension is `ERP_RFQ_Automation.Agent.AgentServiceCollectionExtensions`).

Nothing else is required for endpoints: the surface is `Controllers/AgentController.cs`,
auto-mapped by the existing `app.MapControllers();`. No `MapAgentEndpoints` needed.

`AddAgentEngine` depends on services already registered before this point:
`INotificationService`, `IOrderService`, `IDashboardRepository`, and the tenant-scoped
`ErpRfqAutomationContext` — so keep the call after those.

### Model-config splice (ALREADY APPLIED — for audit only)

EF's `OnModelCreatingPartial` hook already has its single implementation in
`Models/ErpRfqAutomationContext.Tenancy.cs`. A C# partial method allows only one
implementation, so the Agent entity config is invoked via a new **defining+implementing**
partial method `ConfigureAgentModel(...)` in `Models/ErpRfqAutomationContext.Agent.cs`, with
a single delegating call added at the end of the Tenancy partial's `OnModelCreatingPartial`:

```csharp
// ==== Sourcing-copilot ("Agent") engine (Agent/) ====
ConfigureAgentModel(modelBuilder);
```

This 1-line edit to the **Tenancy partial** (not the main scaffolded context file) is the
only source edit outside new files + the migration. It is already in the working tree.

---

## 2. appsettings.json — `Agent` block

```json
"Agent": {
  "Anthropic": {
    "ApiKey": "",
    "Model": "claude-sonnet-5",
    "MaxTokens": 2048
  }
}
```

**Real vs. mock LLM selection** happens in `AddAgentEngine` on presence of the key:

- `ApiKey` **empty** → `MockAgentLlm` (deterministic, NO network). The whole engine —
  chat, tools, guardrails, approvals, audit — is fully demoable with no key.
- `ApiKey` **set** → `AnthropicAgentLlm` (HTTP to `https://api.anthropic.com/v1/messages`,
  header `anthropic-version: 2023-06-01`, tool-use API, full tool loop).

Provide the key via user-secrets / env (`Agent__Anthropic__ApiKey`) in real deployments.

---

## 3. Migration

Created (NOT applied): `Migrations/20260716125034_AddAgentEngine.cs`. Creates 5 tables only —
`AgentSessions`, `AgentMessages`, `AgentApprovals`, `AgentAuditLogs`, `AgentPolicies` — and
touches no existing table. Apply on Neon when ready:

```bash
# from Backend/ERP_RFQ_Automation
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update
```

(To regenerate: `... dotnet ef migrations add AddAgentEngine`. To drop: `ef migrations remove`.)

Timestamps use `now()` server defaults; runs under `Npgsql.EnableLegacyTimestampBehavior`
(already set in Program.cs). jsonb columns: `ToolInput`, `ToolResult`, `InputJson`,
`ResultJson`, `PerToolOverrides`.

---

## 4. Tools

| Tool | Mutation | What it does |
|------|----------|--------------|
| `search_rfqs` | no | Page RFQs by number / buyer name. |
| `get_rfq` | no | One RFQ + line items by id. |
| `search_suppliers` | no | Page suppliers by name / tags / email. |
| `search_leads` | no | Page inbound leads. |
| `search_quotes` | no | Page quotes (totals, validity). |
| `search_orders` | no | Page orders (totals, dates). |
| `get_dashboard_summary` | no | Tenant dashboard KPIs (via `IDashboardRepository`). |
| `recommend_award` | no | Advisory multi-criteria (price 50% / lead time 25% / success 25%) supplier award recommendation for an RFQ. Records nothing → not a mutation. |
| `dispatch_rfq_to_supplier` | **yes** | Emails an RFQ invitation via `INotificationService.SendRfqToSupplierAsync`. |
| `create_order_from_quote` | **yes** | Creates a draft order via `IOrderService.CreateOrderFromQuoteAsync`; value cap uses the quote total. |

Mutations are routed through the guardrail engine by the orchestrator (tools never see
guardrails). Every mutation decision (`Executed` / `Held` / `Denied` / `Failed` / `Rejected`)
is written to `AgentAuditLogs`.

### Guardrail policy (per tenant, `AgentPolicies`)
`AutonomyLevel` {Observe, Suggest, Act}, `MaxAutoAwardValue`, `MaxAutoOrderValue`,
`RequireApprovalForAwards|Orders|SupplierEmails`, `PerToolOverrides` (jsonb map
`tool → allow|require_approval|deny`, highest precedence). **Default when a tenant has no
row:** `Suggest` + every mutation requires approval (conservative). Precedence:
per-tool override → autonomy level → category flag → value cap.

---

## 5. HTTP API (all `[Authorize]`, tenant from `businessUnitId` claim)

- `POST /api/agent/chat` `{ sessionId?, message }` → **SSE** (`text/event-stream`). Lines:
  `{type:"session",sessionId}`, `{type:"token",text}`, `{type:"tool_call",name,input}`,
  `{type:"tool_result",name,ok,summary}`, `{type:"approval_required",approvalId,toolName,summary}`,
  `{type:"done",messageId}`, `{type:"error",message}`. Each is one `data: {json}\n\n` frame,
  flushed immediately (response buffering disabled).
- `GET  /api/agent/sessions` → `[{id,title,updatedOn,messageCount}]`
- `GET  /api/agent/sessions/{id}` → `{id,title,messages:[{role,content,toolName?,toolResultSummary?,createdOn}]}`
- `GET  /api/agent/approvals?status=pending` → `[{id,toolName,summary,status,requestedOn}]`
- `POST /api/agent/approvals/{id}/approve` → re-invokes the stored tool+input through the
  orchestrator's executor (approval IS the gate; no re-guardrail), marks Executed/Failed,
  audits → `{id,status,resultSummary}`
- `POST /api/agent/approvals/{id}/reject` → marks Rejected, audits → `{id,status,resultSummary}`
- `GET  /api/agent/audit?take=100` → `[{id,actor,toolName,decision,summary,createdOn}]`
- `GET  /api/agent/policy` / `PUT /api/agent/policy` → tenant `AgentPolicy`

### SSE notes
Implemented natively (no library): the controller sets `text/event-stream`, disables
buffering via `IHttpResponseBodyFeature.DisableBuffering()`, and writes each orchestrator
`AgentStreamEvent` as a flushed `data:` frame. The orchestrator yields an
`IAsyncEnumerable<AgentStreamEvent>`, so streaming is real (not buffered-then-flushed). No
non-streaming fallback was needed.

---

## 6. Repo gaps / decisions (no existing repo or controller was modified)

- **Reads use `context.Set<T>()` directly**, not the existing repositories. The repos return
  paginated DTOs shaped for their own screens; the tools need compact, agent-friendly JSON
  and simple `query/page/pageSize`. The tenant global query filter (ADR-0005) enforces
  isolation on every `Set<T>()` read, so this is safe. `Supplier`/`Product`/`Customer` carry a
  nullable `Buid` (shared master data visible cross-tenant by design) — the filter handles it.
- **`get_dashboard_summary`** → existing `IDashboardRepository.GetDashboardDataAsync`.
- **`create_order_from_quote`** → existing `IOrderService.CreateOrderFromQuoteAsync` (no new
  method needed; business-rule exceptions are surfaced as tool failures).
- **`dispatch_rfq_to_supplier`** → existing `INotificationService.SendRfqToSupplierAsync`.
- **`recommend_award`** — no existing comparison method existed; computed in-tool over
  `Rfqitem` + `Supplier` (`SuccessRate`) via the DbContext. Purely advisory (records nothing)
  so it is a read, per the contract's guidance.
- **Claims**: user id from `sub`/`ClaimTypes.NameIdentifier`, name from `email`, tenant from
  `businessUnitId` — matching the existing JWT issued by `AuthRepository`.

---

## 7. Deviations from the brief
1. One 1-line splice into the **Tenancy** partial (`ConfigureAgentModel(modelBuilder);`) —
   unavoidable because `OnModelCreatingPartial` already has its single allowed implementation
   there; a second partial hook was introduced in `ErpRfqAutomationContext.Agent.cs`. The main
   scaffolded context file (`ErpRfqAutomationContext.cs`) and Program.cs were NOT edited.
2. `recommend_award` is a **read** (advisory, records nothing) as the contract permits.
3. No new NuGet packages (plain `HttpClient` + `System.Text.Json`).
