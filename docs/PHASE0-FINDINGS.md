# Nexora — Phase 0 Findings Ledger

Evidence-based discovery of the existing build, oriented toward **client-demo
readiness** and release integrity. Statuses: `OPEN` / `IN_PROGRESS` / `FIXED` /
`VERIFIED` / `BLOCKED` / `ACCEPTED_RISK` / `REJECTED`.

Severity: **P0** release-blocker · **P1** critical · **P2** major · **P3** improvement.
Demo impact: **blocker** (breaks/embarrasses in a live demo) · **risk** · **cosmetic** · **none**.

> Discovery workstreams: ARCH (architecture/lifecycle), ING (ingestion truth
> layer), FIN (financial control), SEC (security/tenant), FE (frontend/UX),
> DATA (data/DB/reliability). SEC/FE/DATA pending at time of writing.

---

## Headline (from the first 3 workstreams)

- ✅ **The RFQ → Quote → Order → Shipment spine is REAL and demonstrable
  end-to-end** — real persistence, real QuestPDF quote generation, real SMTP,
  order-from-quote, shipment with status history + auto order-advance. This is
  the safe demo path.
- ⚠️ **Lead → RFQ is disconnected** (ARCH-01): accepting a lead only flips a
  status; it never creates an RFQ, and ingested RFQs don't link back to a lead.
  You cannot currently demo a lead *becoming* an RFQ automatically.
- 🚨 **The "evidence-first truth layer" is mostly aspirational** (ING-01/02/03):
  a genuine canonical model with per-field provenance exists but is wired to
  **one** hand-keyed XLSX template path only — the real email/PDF/AI intake has
  no field-level provenance, can silently lose a lead, and treats unvalidated
  LLM output as truth.
- 🚨 **Financial controls are not yet trustworthy** (FIN-01/02/03/05): tax is
  **never calculated** (taken from the client payload), order totals are
  client-trusted, there is no min-margin/max-discount control, and sent quotes
  remain editable. Decimal types themselves are correct (no float-for-money).
- ⚠️ **Demo-DB fragility** (ARCH-03): status logic is keyed to hardcoded
  SetupMaster PK magic numbers; a DB seeded in a different order miscolors or
  breaks statuses across the app.

---

## ARCH — Architecture & lifecycle wiring

Module status: Auth ✅ · RFQ ✅ · Quote ✅ · Order ✅ · Shipment ✅ · Customer ✅ ·
Supplier ✅ · Product ✅ · Dashboard ✅ (minor hardcoded baselines) · Setup ✅ ·
**Lead/ingestion-entry ⚠️ partial** · **Inventory (stock) ❌ disconnected**.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| ARCH-01 | P1 | OPEN | risk | Lead→RFQ disconnected: accept only flips status; ingested RFQs set no `LeadId` | `LeadRepository.cs:133-144`; `RfqUploaderService.cs:130-142`; `ManualUploadService.cs:779-790` | Add `POST /api/Lead/{id}/convert-to-rfq` creating an Rfq (+LeadId) from the accepted lead's items |
| ARCH-02 | P1 | OPEN | risk | Two incompatible Quote status-ID schemes by creation path (31 vs 42/43/44) | `RfqRepository.cs:303` vs `QuoteService.cs:654-668`, `QuoteRepository.cs:217-218` | Standardize one QuoteStatus set resolved from SetupMaster by code |
| ARCH-03 | P1 | OPEN | risk | Pervasive hardcoded SetupMaster PK magic numbers for all statuses | `LeadRepository.cs:141,168,192`; `RfqRepository.cs:285,303`; `QuoteService.cs:656`; `OrderService.cs:128` | Resolve status IDs by SetupType+SetupCode everywhere (pattern already at `OrderService.cs:44-55`) |
| ARCH-04 | P1 | OPEN | risk | Multi-step conversions not atomic (no Unit of Work) | `OrderService.cs:109-133,276-288`; `ShipmentController.cs:117-130` | Wrap conversions in a DB transaction / single SaveChanges |
| ARCH-05 | P1 | OPEN | risk | Duplicate doc numbers + non-idempotent creates on retry (max+1, no unique index) | `OrderRepository.cs:88-111`; `ShipmentRepository.cs:74-97`; `QuoteService.cs:86-110` | Unique index on (BusinessUnitId, Number) + sequence/idempotency token |
| ARCH-06 | P2 | OPEN | risk | Inventory disconnected; stock never decremented on order/shipment | no InventoryController; `ErpRfqAutomationContext.cs:586` | Decrement QtyOnHand / post movement inside order/shipment txn; add read endpoint |
| ARCH-07 | P2 | OPEN | risk | Order/Shipment/Dashboard controllers lack `[Authorize]`; BU falls back to query param | `OrderController.cs:10-12`; `ShipmentController.cs:13-15`; `DashboardController.cs:8-10` | Add `[Authorize]`, require BU claim, drop query-param fallback (see SEC) |
| ARCH-08 | P2 | OPEN | cosmetic | `CreatedBy/ModifiedBy` hardcoded "System" on order writes | `OrderService.cs:85,103,180,253` | Thread authenticated user from claims |
| ARCH-09 | P2 | OPEN | risk | NRE if RFQ create/update omits `Rfqitems` (500 not 400) | `RfqController.cs:125-126,209-210` | Null-coalesce items to empty |
| ARCH-10 | P2 | OPEN | blocker | Approve rolls back whole RFQ→Quote if no recipient email | `RfqController.cs:296-301` | Decouple quote generation from email; queue/flag the send |
| ARCH-11 | P3 | OPEN | cosmetic | RFQ-sourced orders default to $0 (unpriced RFQ items) | `OrderService.cs:190-204` | Warn/block order-from-RFQ when line prices null; route via Quote |
| ARCH-12 | P3 | OPEN | cosmetic | Dashboard radar benchmarks hardcoded (60/90) | `DashboardRepository.cs:122-123` | Source targets from config/SetupMaster |

---

## ING — Ingestion truth layer (highest product priority)

Five parallel divergent intake pipelines (IMAP→AI, manual upload→AI, folder table-parsers, XLSX lead template, XLSX RFQ template). Canonical evidence model (`CanonicalRfqDocument`, `CanonicalRfqNormalizer`, `SchemaVersion="rfq-canonical/v1"`) is wired to the fixed 11-column XLSX template path **only**.

Format support: PDF native ✅ (flattened) · **scanned PDF ❌ no OCR fallback** · XLSX/DOCX/PPTX ✅ (flattened, structure lost) · images ✅ Tesseract(eng) · **legacy .doc ❌** · **.eml/.msg ❌**.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| ING-01 | P0 | OPEN | risk | **Silent data loss**: pre-classifier reject marks email Seen with no DB row / no raw copy | `EmailService.cs:121,169-173` | Persist EmailIngest + raw .eml for every fetched message *before* classification; add rejected/low-signal status |
| ING-02 | P0 | OPEN | blocker | **No field-level provenance** on live AI path; per-field confidences discarded | `ILLMService.cs:9-48`; `EmailService.cs:406,414,554`; `ManualUploadService.cs:285,328` | Persist per-field value+confidence+source anchor via the canonical model (new tables) |
| ING-03 | P0 | OPEN | risk | **AI unvalidated**: model self-confidence is the only gate; regex "JSON repair" can rewrite values | `OllamaLlmService.cs:258-323,325-346` | Schema/grammar-constrained output; validate values exist in source; reject not patch malformed JSON |
| ING-04 | P1 | OPEN | risk | Scanned/image-only PDFs yield zero text (no OCR fallback for PDF pages) | `EmailService.cs:557-597`; Docnet referenced, unused | Rasterize low-text PDF pages (Docnet) → Tesseract; mark OCR pages lower-confidence |
| ING-05 | P1 | OPEN | risk | No/harmful dedup: subject-equality drops distinct RFQs; missing Message-ID → random GUID defeats unique index | `EmailService.cs:157,162-167`; `ErpRfqAutomationContext.cs:260` | Dedup on content hash (headers+body+attachment digests) |
| ING-06 | P1 | OPEN | risk | No thread reconstruction (In-Reply-To/References ignored) | `EmailService.cs` (subject-only grouping) | Parse Message-ID/In-Reply-To/References; version amendments |
| ING-07 | P1 | OPEN | risk | Silent partial loss: LLM input truncation drops line items, lead saved "complete" | `EmailService.cs:692-740`; `ManualUploadService.cs:676-686` | Chunk large docs (map/reduce); mark truncation as blocking NeedsReview |
| ING-08 | P1 | OPEN | risk | Structure flattened to bag-of-words before AI (rows/cols lost) | `EmailService.cs:598-675` | Preserve table topology + cell coordinates as evidence |
| ING-09 | P1 | OPEN | risk | Fetch window = NotSeen + last-7-days; human opening mail marks Seen → skipped | `EmailService.cs:100-102` | Track per-mailbox UID high-water mark; configurable backfill |
| ING-10 | P2 | OPEN | risk | Duplicate leads on re-import across paths (GUID MessageIds, filename+timestamp keys) | `LeadUploaderService.cs:144-158`; `ManualUploadService.cs:166,781` | Idempotency key from file content hash; upsert |
| ING-11 | P2 | OPEN | risk | Rejected manual uploads lose original bytes (saved only in success branch) | `ManualUploadService.cs:173,335` | Persist raw upload immutably on receipt, before extraction |
| ING-12 | P2 | OPEN | cosmetic | Canonical evidence computed then discarded (only summary string persisted) | `RfqUploaderService.cs:128-171,200-207` | Persist CanonicalRfqDocument/SourceEvidence as first-class rows |
| ING-13 | P2 | OPEN | risk | Raw email store not immutable/verifiable (mutable local .eml, no hash/WORM) | `EmailService.cs:52-56,185-187` | Object storage + content hash + retention lock; verify digest on read |
| ING-14 | P3 | OPEN | cosmetic | Legacy .doc and .eml/.msg claimed/expected but unhandled (silent empty extraction) | `EmailService.cs:847-861` | Add FreeSpire.Doc + MimeKit parsers, or reject clearly |
| ING-15 | P1 | OPEN | risk | FolderService saves attachments via `Task.Run` over shared scoped DbContext (not thread-safe); folder processing fire-and-forget, no concurrency guard | `FolderService.cs:1027-1059`; `EmailController.cs:72` | Use a scoped context per task; add a processing lock |

---

## FIN — Financial control (CPA review)

Good: **every monetary field is C# `decimal`** (no float-for-money); quote line/header math is deterministic in code; the LLM never prices. Bad: tax/discount/FX/controls are largely missing or client-trusted.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| FIN-01 | P0 | OPEN | risk | **Tax is invented, never calculated** — TaxAmount copied from client payload; `Taxis.TaxRate` never read | `QuoteService.cs:71,158`; `OrderService.cs:99`; `Taxis.cs:14` | Resolve Taxis by BU/country/state/effective-date; compute tax server-side; ignore client tax |
| FIN-02 | P0 | OPEN | risk | **Order totals trusted from client payload** (discount+tax+total from DTO) | `OrderService.cs:38-41,100`; `DTOs/OrderDto.cs:31-41` | Recompute discount+tax server-side; client amounts display-only |
| FIN-03 | P0 | OPEN | risk | No min-margin / max-discount control (below-cost quotes accepted) | `QuoteService.cs:229,258` | Enforce max-discount % + min-margin floor vs FinalLandedCost |
| FIN-04 | P1 | OPEN | risk | No maker-checker/approval before finalize/send | `QuoteService.cs:617-659,661-699` | Approval state/role gate before leaving DRAFT / before send |
| FIN-05 | P1 | OPEN | risk | **Finalized quotes mutable** (no version lock); stored total can diverge from sent PDF | `QuoteService.cs:112-189` | Reject edits (or fork version) once StatusId ≥ SENT; snapshot sent totals |
| FIN-06 | P1 | OPEN | risk | Order-from-Quote header math doesn't foot (discount rate stored as amount, tax null) | `OrderService.cs:250-252,359-363` | Carry true gross subtotal, computed discount amount, summed line tax |
| FIN-07 | P1 | OPEN | risk | No FX/multi-currency (ExchangeRate stored, never applied); mixed-currency lines summed as-is | `Currency.cs:16` | Convert to base currency via effective rate before summing; stamp rate |
| FIN-08 | P1 | OPEN | risk | Duplicate quotes/orders on retry (non-unique numbers, race, no txn/idempotency) | `QuoteService.cs:86-110`; `OrderRepository.cs:88-108`; `ErpRfqAutomationContext.cs:440,746` | Unique constraint + sequence-in-txn + idempotency key + "already ordered" guard |
| FIN-09 | P1 | OPEN | cosmetic | Rounding inconsistent: 6-dp lines vs 2-dp headers, no explicit rounding | Context:561-562,827-828 vs 484-491,760 | Round each line to currency scale before summing; same scale for lines+headers |
| FIN-10 | P2 | OPEN | none | Two divergent quote-calc engines (service vs repository) | `QuoteService.cs:239-260` vs `QuoteRepository.cs:139-141,188-190` | Delete/redirect the dead engine |
| FIN-11 | P2 | OPEN | cosmetic | PDF "Additional Discount" reconstruction wrong when tax present | `QuoteService.cs:387-397` | Persist header discount amount explicitly |
| FIN-12 | P3 | OPEN | cosmetic | No range validation on qty/price/discount/tax sign | `QuoteService.cs:229` | Add [Range]/server guards rejecting non-positive price/qty |

---

## SEC — Security & tenant isolation

Verdict: **not safe to demo on a shared/multi-tenant instance** — a client poking
the API would see other tenants' data. JWT validation itself is correct
(issuer/audience/lifetime/signing enforced); no raw SQL anywhere (EF LINQ). The
failures are in **authorization and tenant scoping**. Safe path: single-tenant,
walkthrough-only demo (client not given API access) + fix the P0s.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| SEC-01 | P0 | OPEN | blocker | Password reset has no ownership/BU/old-password check → cross-tenant account takeover | `UserController.cs:243-251`; `UserRepository.cs:227-233` | Require caller==target or admin perm; verify current password; scope to BU claim; rotate tokens |
| SEC-02 | P0 | OPEN | blocker | `DashboardController` fully unauthenticated, reads by route businessUnitId | `DashboardController.cs:10,19-23` | Add `[Authorize]`; derive BU from JWT claim only |
| SEC-03 | P0 | OPEN | blocker | `OrderController` no `[Authorize]` + client-BU fallback → unauth cross-tenant order read/write | `OrderController.cs:12,29-30,50-51,75-76,152-157` | Add `[Authorize]`; remove BU param; use claim |
| SEC-04 | P0 | OPEN | blocker | `ShipmentController` no `[Authorize]` + client-BU fallback | `ShipmentController.cs:15,31-32,71-72,120-129` | Add `[Authorize]`; BU from claim |
| SEC-05 | P0 | OPEN | blocker | `SupplierQuotedItemController` no `[Authorize]` → leaks/edits supplier pricing cross-tenant | `SupplierQuotedItemController.cs:15,29-30,48-49,95-96` | Add `[Authorize]`; BU from claim |
| SEC-06 | P0 | OPEN | blocker | `UserController` inverts tenant guard (`param ?? claim`) → cross-tenant user read/delete + privilege escalation | `UserController.cs:44,81,100-153,166-224,275` | Use claim authoritatively; admin perm for user CRUD; validate RoleId/Buid vs tenant |
| SEC-07 | P1 | OPEN | risk | RFQ Approve unscoped → cross-tenant write + quote/pricing emailed to attacker address | `RfqController.cs:259-271`; `RfqRepository.cs:275-327` | Scope by BU claim; ignore client recipientEmail/approvedBy |
| SEC-08 | P1 | OPEN | risk | Custom RBAC applied to exactly 1 endpoint → no function-level authz (logged-in = allowed) | `Program.cs:56-70` vs only `RfqController.cs:337` | Apply policy attributes to every mutating action; default-deny |
| SEC-09 | P1 | OPEN | risk | Anonymous file download, no tenant/authz scoping | `FileController.cs:25-27` (`[AllowAnonymous]`) | Require `[Authorize]`; resolve by attachment id scoped to BU; no raw path |
| SEC-10 | P1 | OPEN | risk | Path traversal / arbitrary file write via multipart filename | `FolderService.cs:495` via `EmailController.cs:48-56` | Sanitize with GetFileName/whitelist; reject rooted/`..`; verify resolved path |
| SEC-11 | P2 | OPEN | risk | Unrestricted upload type → user images to web-served wwwroot → stored XSS | `UserController.cs:108-123`; `ManualUploadService.cs:375-384` | Validate by magic bytes; whitelist; serve as attachment / separate origin |
| SEC-12 | P2 | OPEN | risk | JWT key falls back to empty string; key length unchecked | `Program.cs:122`; `AuthRepository.cs:98` | Fail fast if key missing; enforce ≥256-bit; secret store |
| SEC-13 | P2 | OPEN | risk | CORS AllowAnyOrigin/Method/Header; HTTPS redirect disabled; no security headers | `Program.cs:97-106,162,164` | Restrict CORS to known origins; HSTS + security headers |
| SEC-14 | P2 | OPEN | risk | Latent unscoped update lookups (defense-in-depth gap) | `ContactRepository.cs:154`; `UserRepository.cs:176`; `QuoteRepository.cs:152` | Filter every write lookup by BU at repo layer |
| SEC-15 | P3 | OPEN | risk | Anonymous tenant enumeration (BU dropdown) + anonymous web-search | `BusinessUnitController.cs:78-79`; `SupplierController.cs:329` | Minimize/throttle login dropdown; auth web-search |
| SEC-16 | P3 | OPEN | cosmetic | Prompt injection via document text; verbose error disclosure (echoes ex.Message) | `OllamaLlmService.cs:348-368`; e.g. `OrderController.cs:40` | Delimit untrusted text; treat fields untrusted; generic errors |

## FE — Frontend demo-readiness & UX

Verdict: **more real than "demo-ready" usually implies** — a working RBAC ERP
front-end, ~45 pages all wired to live services, consistent TanStack Query,
session survives refresh, no mock data / no stub pages. Demo risk is almost
entirely **feedback visibility on writes** (FE-01) and a few **dead controls**.

**Safest click-path:** Login (as **Business Unit 1**) → Dashboard → Suppliers
(strongest screen) → Customers → Inventory▸Products → Security▸Users/Roles →
*view* existing Lead/RFQ/Quote/Order (read-only). Avoid live *creation* of
Quotes/RFQs on stage until FE-01 is fixed.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| FE-01 | P0 | OPEN | blocker | `react-hot-toast` used on 6 pages but no `<Toaster>` mounted → all success + validation feedback silent | `main.tsx:23-39`; `CreateQuotePage.tsx:109-163`; `ProcessRFQPage.tsx:1142-1159` | Mount `<Toaster/>` (one line) or migrate those 6 to notistack |
| FE-02 | P1 | OPEN | risk | No true auth guard; logged-out deep-link loops instead of redirecting to login | `App.tsx:55`; `PermissionGuard.tsx:26-28` | Auth gate: no token → Navigate to /login |
| FE-03 | P1 | OPEN | blocker | Dashboard hardcodes `GET /api/Dashboard/1` → wrong-tenant/zero KPIs for BU≠1 | `DashboardPage.tsx:154` | Use `userData.businessUnitId`; add error state |
| FE-04 | P2 | OPEN | risk | Dead filter chips on Shipments (In Transit/Delivered/Overdue do nothing) | `ShipmentListPage.tsx:261-263` | Wire to status filter or remove |
| FE-05 | P2 | OPEN | risk | Navbar global "Search anything… ⌘K" is decorative Typography | `Navbar.tsx:120-138` | Real command palette or remove |
| FE-06 | P2 | OPEN | risk | Notification bell (with unread dot) + Account Profile are no-ops | `Navbar.tsx:201-208,256` | Add handler/panel or hide |
| FE-07 | P2 | OPEN | risk | Native `alert()` dialogs in a polished MUI app | `QuoteFormatPage.tsx:58`; `CreateOrderPage.tsx:184-188` | Replace with snackbar/inline validation |
| FE-08 | P2 | OPEN | risk | No ErrorBoundary → any render throw white-screens the SPA | grep: none in src | Wrap App + routes in ErrorBoundary |
| FE-09 | P2 | OPEN | cosmetic | Single 2.5MB JS chunk, zero code-splitting; 262KB eager logo | `dist/assets/index-*.js`; `App.tsx:1-49` | React.lazy per route; manualChunks |
| FE-10 | P3 | OPEN | risk | i18n skin-deep; page bodies hardcoded English → mixed-language if switched | `QuoteFormatPage.tsx:89,124`; `i18n.ts` | Externalize strings or hide language switcher for demo |
| FE-11 | P3 | OPEN | risk | 401 interceptor hard-reloads on ANY 401 incl. background calls → possible loop | `axiosInstance.ts:26-29`; `AuthContext.tsx:99-112` | Redirect only for auth-required calls; debounce |
| FE-12 | P3 | OPEN | cosmetic | No token-expiry handling; jwt-decode installed but unused | `package.json:25` | Decode exp; pre-emptive refresh/redirect |
| FE-13 | P3 | OPEN | risk | ~half of list pages have no explicit isError UI (empty grid looks like "no data") | `LeadsPage.tsx`; `OrderListPage.tsx`; `UsersPage.tsx` | Standard error state + retry |
| FE-14 | P3 | OPEN | cosmetic | Hardcoded fallbacks ("Abdullah Afzal"/"Admin"); reject reason hardcoded 1 | `Navbar.tsx:227-230`; `LeadDetailPage.tsx:82` | Initials/generic fallback; reject-reason selector |

## DATA — Data/DB & reliability

Verdict: **conditionally stand-uppable, but fragile.** The app runs only if (a) the
**remote** SQL Server `168.231.72.175` is reachable AND (b) it runs in Development
(so the git-ignored `appsettings.Development.json` overlays the placeholders).
**No local/seed/migration fallback exists.** Bright spot: email ingestion
idempotency is solid (unique `EmailIngest.MessageId` + dedup + dup-key catch).
Correction: the health/metrics/tracing "reliability hardening" from the branch's
git history is in **opstrax-supplyops-v1, NOT Nexora** — Nexora has none.

Note (reconciles with AI config): `Program.cs:85-88` hardcodes the Ollama
`HttpClient` to `http://localhost:11434/`, overriding the `ollama.com` cloud URL
in config — so AI extraction actually hits **local** Ollama (which is reachable
on this machine), not the cloud key.

| ID | Sev | Status | Demo | Finding | Evidence | Repair |
|---|---|---|---|---|---|---|
| DATA-01 | P0 | OPEN | blocker | Background DB query outside try/catch → one remote-DB blip crashes the whole host (default StopHost) | `EmailBackgroundService.cs:41-46` | Move interval query inside try; catch/log + default interval; set exception behavior Ignore |
| DATA-02 | P0 | OPEN | blocker | Hard dependency on one remote SQL Server; no local/seed/docker path | `Program.cs:20-21`; `appsettings.Development.json:10` | docker-compose SQL + baseline schema/seed, or EF baseline migration on LocalDB |
| DATA-03 | P1 | OPEN | risk | No EF migrations = no schema-as-code/rollback (pure scaffold) | no `Migrations/`; `ErpRfqAutomationContext.cs:96` | Baseline `ef migrations add InitialCreate`; gate releases on it |
| DATA-04 | P1 | OPEN | risk | No connection resilience / command timeout (no EnableRetryOnFailure) | `Program.cs:20-21` | Add EnableRetryOnFailure + CommandTimeout |
| DATA-05 | P1 | OPEN | risk | No health/readiness/liveness checks | `Program.cs` (none) | AddHealthChecks().AddSqlServer + MapHealthChecks("/health") |
| DATA-06 | P1 | OPEN | risk | Ollama HttpClient hardcoded to localhost, contradicts config | `Program.cs:85-88` vs `appsettings.json:19` | Bind BaseAddress from Ollama:BaseUrl config |
| DATA-07 | P1 | OPEN | risk | Placeholder config + git-ignored dev secrets → silent misconfig if env≠Development | `appsettings.json:10,13` | Env-var/secret substitution; fail fast on placeholder detection |
| DATA-08 | P2 | OPEN | risk | Tenancy inconsistent: Customer/Supplier/Product/Contact/Inventory lack BusinessUnitId → global master data | `Customer.cs`, `Supplier.cs`, `Product.cs` | Add BusinessUnitId(+index) or document as shared reference data |
| DATA-09 | P2 | OPEN | risk | Business doc numbers not unique — `Rfqno` no index; OrderNo/QuoteNo non-unique | `ErpRfqAutomationContext.cs:440,746,847-880` | Unique indexes on (BU, Rfqno/OrderNo/QuoteNo) |
| DATA-10 | P2 | OPEN | risk | No retry/dead-letter for failed email parsing → silent permanent RFQ drop | `EmailService.cs:162-167,199-203` | Retry/backoff + status sweeper to reprocess Pending/Failed |
| DATA-11 | P2 | OPEN | cosmetic | All FKs ClientSetNull, pervasive nullable FKs (RFQ can lack customer/status) | 32× in `ErpRfqAutomationContext.cs`; `Rfq.cs:48-50` | Define cascade/restrict explicitly; tighten required FKs |
| DATA-12 | P3 | OPEN | risk | No global exception handler/observability; duplicate AddControllers() | `Program.cs:18,90,155-168` | UseExceptionHandler + structured logging; dedupe |
