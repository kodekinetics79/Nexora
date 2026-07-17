# SLA & Deadline Engine + Quote Outcome Capture — Wiring Guide

Work packages WP-A2 (SLA/deadline engine) and WP-A4 (quote outcome capture).
All code lives in `Sla/` (namespace `ERP_RFQ_Automation.Sla`) plus two `Models/`
partials. **Nothing here is wired into `Program.cs` yet** — that file is owned
elsewhere this wave. Add the lines below.

---

## 1. Program.cs lines to add

```csharp
// ==== SLA & deadline engine + quote outcome capture (Sla/) ====
// After AddNotifications(...) — SlaNotifications depends on IEmailSender.
builder.Services.AddScoped<ERP_RFQ_Automation.Sla.IQuoteOutcomeService, ERP_RFQ_Automation.Sla.QuoteOutcomeService>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Sla.ISlaNotifications, ERP_RFQ_Automation.Sla.SlaNotifications>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Sla.SlaSweepWorker>();
```

Notes:
- `SlaNotifications` is a **singleton** (depends only on the singleton
  `IEmailSender` + `ILogger`). `QuoteOutcomeService` is **scoped** (DbContext +
  `IQuoteService`). The worker creates a fresh scope per 5-minute iteration.
- `SlaController` is a plain `[ApiController]` — discovered automatically by the
  existing `AddControllers()`; no extra registration.
- **No startup seeder is required.** SetupMaster seeding is lazy/idempotent
  inside `QuoteOutcomeService` (see §4). If an eager warm-up is preferred, call
  `GetOutcomeReasonsAsync(bu)` per tenant at startup — optional.
- `QuoteController` now constructor-injects `IQuoteOutcomeService`; the app will
  fail on the first Quote request until the `AddScoped` line above is added.

## 2. Schema for the lead's migration (NO migration was generated)

Configured in `Models/ErpRfqAutomationContext.Sla.cs` via the partial-method
splice: `ConfigureSlaModel(modelBuilder);` was added to
`OnModelCreatingPartial` in `Models/ErpRfqAutomationContext.Tenancy.cs`
(mirroring `ConfigureAgentModel`).

### Table `SlaPolicies` (entity `Sla/SlaPolicy.cs`, one row per BU)
| Column | Type | Notes |
|---|---|---|
| Id | bigint identity | PK |
| BusinessUnitId | bigint | unique index `UX_SlaPolicies_BU`; tenant filter |
| UnassignedHours | integer | default row value 2 |
| WarnDaysBeforeClose | integer | default 3 |
| CriticalDaysBeforeClose | integer | default 1 |
| StaleQuoteDays | integer | default 7 |
| QuoteAutoExpireDays | integer | default 14 |
| ApprovalEscalationHours | integer | default 4 |
| DeadlineBufferHours | integer | default 12 (reserved, not yet consumed by the sweep) |
| CreatedOn | timestamp | server default `now()` |
| UpdatedOn | timestamp | server default `now()` |

Defaults are applied in code via `SlaPolicy.Default(bu)` when no row exists
(AgentPolicy pattern) — the integer defaults above are initializer values, not
DB defaults.

### Table `SlaEvents` (entity `Sla/SlaEvent.cs`, append-only)
| Column | Type | Notes |
|---|---|---|
| Id | bigint identity | PK |
| BusinessUnitId | bigint | tenant filter |
| EntityType | varchar(40) | `lead` \| `lead-unassigned` \| `quote` \| `quote-stale-digest` \| `approval` |
| EntityId | bigint | lead/quote id; owner user id for digests; first 8 bytes of the approval Guid for approvals |
| Level | varchar(20) | `warn` \| `critical` \| `overdue` \| `stale` \| `expired` \| `escalated` |
| CreatedOn | timestamp | server default `now()` |

Non-unique index `IX_SlaEvents_BU_Entity_Level` on
(BusinessUnitId, EntityType, EntityId, Level). Dedup is lookup-before-insert
(single worker instance); deliberately **not** a unique constraint because the
daily digest key allows one row per day.

### New `Quotes` columns (partial `Models/Quote.Outcome.cs`)
| Column | Type | Notes |
|---|---|---|
| SentOn | timestamp NULL | stamped by `SendQuoteEmailAsync` when marking SENT |
| RespondedOn | timestamp NULL | mark-responded endpoint; also stamped by any manual terminal outcome |
| OutcomeReasonId | bigint NULL | **loose FK** to `SetupMaster.SetupId` (SetupType `QuoteOutcomeReason`). Mapped as a plain column with no navigation/constraint so the scaffolded SetupMaster stays untouched; names are batch-resolved in repositories. The lead may add a real FK in the migration if desired. |
| OutcomeOn | timestamp NULL | when the terminal outcome was recorded |
| OutcomeNote | varchar(500) NULL | free-text note |

## 3. IQuoteOutcomeService contract (for other agents)

```csharp
public interface IQuoteOutcomeService
{
    // outcome: "won" | "lost" | "expired"; reason REQUIRED for lost/expired,
    // optional for won. Terminal quotes immutable except manager/admin
    // (Users.RoleId -> SetupMaster SetupType "role", name contains admin/manager).
    Task<QuoteResponseDTO> SetOutcomeAsync(long quoteId, long businessUnitId, string actorEmail,
        string outcome, string? reasonCode = null, string? note = null, CancellationToken ct = default);

    // System path (sweep). Only acts on quotes still in SENT; returns false otherwise.
    // Does NOT stamp RespondedOn (the customer never answered).
    Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default);

    Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default);

    // Seeds-if-absent, then returns the BU's governed picklist.
    Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(long businessUnitId, CancellationToken ct = default);
}
```

State machine (status via SetupMaster `QuoteStatus` code, resolved BU-first):

```
DRAFT --SendQuoteEmailAsync--> SENT (SentOn stamped)
SENT  --outcome won-----------> ACCEPTED  (OutcomeOn, RespondedOn, optional reason)
SENT  --outcome lost----------> REJECTED  (OutcomeOn, RespondedOn, reason required)
SENT  --outcome expired-------> EXPIRED   (OutcomeOn, RespondedOn, reason required)
SENT  --sweep auto-expire-----> EXPIRED   (OutcomeOn, reason AUTO_EXPIRED, NO RespondedOn)
ACCEPTED/REJECTED/EXPIRED: immutable; manager/admin may re-run SetOutcomeAsync to correct.
```

`QuoteService.TransitionStatusAsync` remains the single transition primitive —
`SetOutcomeAsync`/`ExpireAsync` delegate to it. Its magic-number ids (42/43/44/45)
were replaced by SetupMaster resolution (`ResolveQuoteStatusIdAsync`, BU-scoped
first, any-BU second) with the old ids kept only as a documented legacy fallback
map for tenants that predate the `QuoteStatus` rows. `EXPIRED` is now a valid
transition code.

### Adapter note for the deduplication agent
`ISlaPolicyReader` should read `Sla/SlaPolicy`: implement it as a thin adapter —
`db.Set<SlaPolicy>().FirstOrDefaultAsync(p => p.BusinessUnitId == bu) ?? SlaPolicy.Default(bu)`
— and map whichever thresholds it needs. Do not duplicate the defaults.

## 4. SetupMaster seeding (idempotent, lazy, per-BU)

`SetupMaster.BusinessUnitId` is **non-nullable**, so shared null-BU rows are not
possible — rows are seeded **per BU on demand** (first picklist fetch or first
outcome/expire call for that BU), guarded by an existence check per code.

- SetupType `QuoteOutcomeReason` (8 rows): PRICE "Price too high", LEAD_TIME
  "Lead time too long", NO_STOCK "Item unavailable", LOST_COMPETITOR "Lost to
  competitor", CUSTOMER_CANCELLED "Customer cancelled", NO_RESPONSE
  "No response", AUTO_EXPIRED "Expired automatically", OTHER "Other".
  `CreatedBy = "system:sla-seed"`. AUTO_EXPIRED is hidden in the manual UI flow.
- SetupType `QuoteStatus` code `EXPIRED`: created-if-absent (checks the BU first,
  then any BU — an existing row anywhere satisfies the BU-agnostic fallback).

## 5. Sweep worker behavior & documented choices

`SlaSweepWorker : BackgroundService`, 5-minute period, fresh DI scope per
iteration, all exceptions logged and swallowed (per-tenant try/catch too — one
bad tenant never blocks the rest). Runs tenant-less, so global filters are
no-ops; every query is explicitly BU-scoped with `IgnoreQueryFilters()`.

1. **Lead deadlines** — leads with a real (>= year 2000) `BidClosingDate`,
   `LeadStatusId` null or 24, no rejected reason. Highest applicable level only:
   past close → `overdue` (assignee + assignee's manager via `Users.ManagerId`),
   within CriticalDays → `critical` (assignee), within WarnDays → `warn`
   (assignee). One SlaEvent per (lead, level) ever. Unassigned leads are skipped
   here — covered by (2).
2. **Unassigned aging** — `LeadStatusId == 24 && AssignTo == null` older than
   `UnassignedHours` (age from `ModifiedDate ?? CreatedDate`). Notifies all BU
   users whose role name contains manager/admin, **once per lead ever**.
   *Choice:* recorded as EntityType `lead-unassigned` + level `warn` (kept the
   standard level vocabulary; the distinct EntityType prevents collision with
   the deadline `warn` for the same lead).
3. **Quote auto-expire** — SENT and `coalesce(ValidUntil, SentOn) +
   QuoteAutoExpireDays < now` → `IQuoteOutcomeService.ExpireAsync(id, "AUTO_EXPIRED")`
   + SlaEvent (`quote`, `expired`).
4. **Stale quotes** — SENT, `SentOn + StaleQuoteDays < now`, `RespondedOn` null.
   Per-owner daily digest (one email listing all their stale quotes).
   *Choice:* daily dedup is **per owner per UTC day** via SlaEvent
   (`quote-stale-digest`, EntityId = owner user id, level `stale`,
   `CreatedOn >= today`); per-quote rows would bloat the ledger without adding
   signal. Owner resolution: `Quote.CreatedBy` matched to a BU user by email,
   then by "First Last"; unmatched owners are logged and skipped. The computed
   `isStale` (+ `daysSinceSent`, `statusCode`) is always exposed on the quotes
   list/detail DTOs regardless of email delivery.
5. **Approval escalation** (bonus, uses `ApprovalEscalationHours`) — pending
   `AgentApproval`s older than the threshold → manager/admin alert once per
   approval. *Choice:* SlaEvent.EntityId is bigint while approval ids are Guids,
   so the dedup key is the Guid's first 8 bytes (never used for reverse lookup).

## 6. Email templates

`SlaNotifications` injects the Notifications module's `IEmailSender` directly
and applies the same never-throw resilience as `NotificationService`.
*Deviation (documented):* `IEmailTemplateRenderer` is name-locked to the static
`EmailTemplates` dictionary; registering the two SLA templates there would mean
editing the Notifications module (owned by another agent this wave). The two
templates — **deadline-alert** (level badge + headline + detail + reference) and
**stale-quotes-digest** (table of quote / customer / sent / waiting) — live in
`SlaNotifications` with identical `{{token}}` substitution semantics. With
`Notifications:Provider = console` (dev default) all sends log to stdout.

## 7. HTTP surface

- `GET  /api/sla/policy` — stored row or default ([Authorize], BU from claim)
- `PUT  /api/sla/policy` — patch-upsert; validates critical <= warn
- `POST /api/Quote/{id}/outcome` — body `{ outcome, reasonCode?, note? }`;
  409 on non-manager correction of a terminal quote
- `POST /api/Quote/{id}/mark-responded`
- `GET  /api/Quote/outcome-reasons` — seeds + returns the governed picklist

## 8. Frontend

- `src/api/services/slaService.ts`, quote service extensions in
  `src/api/services/quoteService.ts`.
- SLA settings card: `src/pages/Setup/Sla/SlaSettingsPage.tsx`, route
  `/setup/sla` in `App.tsx` (PermissionGuard module "UOM" — same guard as
  /setup/master and /setup/price-structure), Sidebar entry "Deadlines & Alerts"
  under Setup.
- Lead deadline chip: `DeadlineChip` in `src/pages/Leads/LeadDetailPage.tsx`
  (overdue red / <= 3 days amber / else neutral; sentinel dates < 2000 hidden
  via `src/utils/dates.ts`).
- Outcome flow: `src/pages/Sales/Quotes/QuoteOutcomeDialog.tsx` used from the
  QuotesPage row action (trophy icon on SENT rows) and the QuoteViewPage
  "Record outcome" button. Status column shows Sent · x days ago / Responded /
  "Stale · no reply for N days" / Won-Lost-Expired chips with reason tooltips.
