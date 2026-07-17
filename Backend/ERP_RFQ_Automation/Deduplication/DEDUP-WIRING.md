# Deduplication + lead-ownership wiring (WP-A1 / WP-A3)

Everything in this document is for the **lead** to splice; nothing here has been
applied to `Program.cs` or the migrations folder by this work package.

## 1. DI registration lines (Program.cs)

Add after the existing repository registrations (both consumers take these as
*optional* constructor parameters, so the app also runs before this splice —
detection and the SLA threshold simply degrade to no-op / flat 2h default):

```csharp
// WP-A3: duplicate-lead detection (Deduplication/)
builder.Services.AddScoped<ERP_RFQ_Automation.Deduplication.ILeadDuplicateDetector,
                           ERP_RFQ_Automation.Deduplication.LeadDuplicateDetector>();

// WP-A1: interim SLA policy reader (flat 2h unassigned threshold).
// The SLA engine (Sla/, other agent) should REPLACE this registration with its
// real tenant-configurable reader when it lands.
builder.Services.AddScoped<ERP_RFQ_Automation.MultiTenancy.ISlaPolicyReader,
                           ERP_RFQ_Automation.MultiTenancy.DefaultSlaPolicyReader>();
```

`INotificationService` is already registered via `AddNotifications(...)` — no change.

## 2. Migration: required new columns on `Lead`

No migration was generated (per constraints). The entity properties live in
`Models/Lead.Duplicate.cs`; EF configuration in
`Models/ErpRfqAutomationContext.Tenancy.cs` (`OnModelCreatingPartial`). When you
run `dotnet ef migrations add`, EF will emit exactly:

| Column (Lead table)  | Type                 | Nullable | Notes                                                       |
|----------------------|----------------------|----------|-------------------------------------------------------------|
| `DuplicateStatus`    | `varchar(20)` / text | yes      | null \| `suspected` \| `confirmed` \| `not_duplicate`       |
| `DuplicateOfLeadId`  | `bigint`             | yes      | id of the OLDER lead of the pair (no FK configured — soft link, matches module conventions) |
| `DuplicateResolvedBy`| `varchar(256)`       | yes      | caller email stamped by POST /api/Lead/{id}/duplicate-resolution |

Plus one index: `IX_Lead_BU_DuplicateStatus` on `(BusinessUnitId, DuplicateStatus)`.

## 3. Duplicate rules implemented (`LeadDuplicateDetector`)

Candidate set: same BU, `LeadStatusId != 25` (null counts as not-rejected),
`CreatedDate > now − 90d`, excluding the lead itself; projection-only queries
capped at 500 rows each, item keys fetched in one batched query.

A pair is a duplicate when:
- (a) normalized `Rfqno` matches exactly (casefold, strip non-alphanumerics, only when non-null), OR
- (b) same customer key (`lower(Clientemail)`, falling back to normalized
  `BuyersName` when the email is null **or an internal pipeline placeholder**
  like `extraction@pipeline.local` — otherwise every extracted lead would share
  one key) AND |BidClosingDate delta| ≤ 2 days (or both null) AND item overlap
  ≥ 0.6, where overlap = |intersection| / min(|A|,|B|) over normalized
  `ManufacturerPartNumber` ∪ `ItemMaterialCode` ∪ `CustomerRfqno`.

The NEWER lead of the pair gets `suspected` with `DuplicateOfLeadId` = older
lead. A lead already `suspected`/`confirmed` is never re-flagged; a pair a human
already marked `not_duplicate` is not re-flagged for the same original.

Call sites (already wired, each in try/catch so detection can never break the
business flow): end of `LeadPersister.PersistAsync` (Extraction/ExtractionWorker.cs)
and `LeadRepository.AcceptLeadAsync`. On flagging, the ORIGINAL lead's assignee
(or an admin/manager of the BU when unassigned) receives the
`lead-duplicate-flagged` email (`NotifyDuplicateLeadAsync`).

## 4. Suggested `src/pages/Leads/LeadsPage.tsx` additions (other agent)

The list endpoint (`GET /api/Lead`) and detail endpoint now return
`duplicateStatus` and `duplicateOfLeadId` on every row. Suggested:

- Badge in the RFQ column when `duplicateStatus === 'suspected'`
  (e.g. amber chip "Possible duplicate") and `'confirmed'` (red chip
  "Duplicate of #{duplicateOfLeadId}"). No badge for null / `not_duplicate`.
- Optional client-side filter toggle "Possible duplicates" that filters loaded
  rows on `duplicateStatus === 'suspected' || duplicateStatus === 'confirmed'`
  (no server param exists yet; add `duplicateStatus` query param to
  `GetLeadListAsync` if a server-side filter is wanted).
- Row action "Review duplicate" navigating to `/procurement/leads/view/{id}`,
  where the banner + resolution actions already exist (this work package).

Resolution endpoint (already live): `POST /api/Lead/{id}/duplicate-resolution`
with body `{ "action": "not_duplicate" | "confirm" }` — 409 when the lead is not
flagged, 403 never (any authorized BU user may resolve).

## 5. Assignment endpoints touched (WP-A1, for reference)

- `POST /api/UnAssignedLead/assign` now returns **403** unless the caller's
  `roleId` claim resolves (SetupMaster `SetupType == "role"`, matched by
  SetupCode/SetupValue containing "admin"/"manager", case-insensitive) to a
  manager/admin role.
- `GET /api/UnAssignedLead` / `/assigned` rows now include `unassignedHours`,
  `isUnassignedOverdue` (threshold via `ISlaPolicyReader`, default 2h),
  `duplicateStatus`, `duplicateOfLeadId`.
- Assignment fires `lead-assigned` email to the assignee; when the assignee
  changes from a different non-null user, the previous assignee gets the
  `lead-reassigned-away` note. Failures are logged, never thrown.
