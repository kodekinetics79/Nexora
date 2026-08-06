# Overnight Remaining Blockers — Commercial Sourcing Pilot

**Date:** 2026-08-06. Separated by who can clear them, because that is what determines sequencing.

---

## A. Product defects — Nexora engineering can clear these

### A-1 (S1) — Ownership does not survive Lead → RFQ conversion. **Partially cleared.**
**Root cause found and fixed.** The blocker was never a missing column. `LeadAssignment`
(`CommercialRouting/CommercialRoutingDomain.cs:142`) is keyed to `LeadId`, bitemporal and
append-only, so an RFQ should *resolve* its owner through its Lead — adding an owner column to
`Rfq` would have created the second ownership model the rules forbid.

The real blocker was that **`SalesApplicationService.UpsertProfileAsync` was reachable from no
controller**, so `sales_rep_profiles` held zero rows, the routing engine's eligibility gate could
never be satisfied, and 44/44 leads routed to `NO_MATCH_EVIDENCE`. Routing was never broken; it
had no reps to choose from. **Cleared:** `POST api/commercial-intelligence/reps/{userId}/routing-profile`
(`Users:Edit` + manager role, tenant from claim, actor from principal, idempotency from header).

**Still open:** RFQ/Quote do not yet *display* the resolved owner (read-side projection), and
conversion does not yet require an owner or a controlled queue. Decision taken: route to the
existing `UnassignedWorkItem` queue with its 4 h SLA, block only when neither exists.

### A-2 (S1) — Critical extraction warnings were decorative. **CLEARED (backend).**
`ConvertCoreAsync` now reads them, before any write. Attention reasons are split into a typed
hard/soft classification on `ResolvedLine` rather than re-derived by matching message text, so
rewording a message can never silently make a hard failure waivable:

- **Hard** (missing quantity, missing unit) — refused outright; **no acknowledgement can waive
  them**, because acknowledging "I don't know how many" produces an RFQ that cannot be quoted.
- **Soft** (no catalog match, low-confidence match, UoM needs review) — require an explicit
  acknowledgement with a reason of at least 5 characters, matching the threshold the
  routing-override and No-Quote paths already enforce.

Audited on the **existing** lifecycle event — `ReasonCode = CONVERTED_WITH_ACKNOWLEDGED_WARNINGS`
and `ReasonNotes` naming every waived line and why. Both fields were previously always null, so
no new audit table was needed. `AcknowledgeAllWarnings` waives the soft set in one action: an
84-line SEC bid list where 60 lines lack a catalog entry would otherwise train operators to click
through 60 boxes without reading, which is worse than no gate.

**Still open:** the Convert page does not yet surface the acknowledgement control (A-8).

### A-3 (S1) — No per-line lineage, and a prerequisite blocks the obvious fix.
`Rfqitem` has no `LeadItemId`. `LinkRfqAsync` re-guesses the mapping afterwards
(`CommercialLineResolutionApplicationService.cs:166-196`): line number → productId → part
identity → *"if the counts match, take the first free row"*. **Do not add the FK yet** —
`ApplyCurrentProjection` deletes and recreates `LeadItems` on each revision, so the FK would be
destroyed on the first amendment. Source-line identity must be made stable first.

### A-4 (S2) — No RFQ business revision axis.
`Rfq` has no revision number; `LifecycleVersion` is an optimistic-concurrency counter that
`Quote` misreads as one (`Models/Quote.CommercialIdentity.cs:35`). `RFQ_REVISION_REQUIRED`
impacts are written (`LeadIdentityApplicationService.cs:868`) and consumed by nothing.

### A-5 (S2) — Inbound supplier reply correlation is absent.
Thread continuation is *detected* (`Ingestion/Triage/EmailTriageService.cs:238`) but never joined
to `SupplierSolicitation` or `ProcurementOutboxMessage.ProviderReference`.
`SupplierQuoteInboxService.cs:40` requires the caller to supply `SupplierSolicitationId` — i.e. a
human matches the reply by hand. **This is the single missing link that makes the outbound→inbox
loop manual**, and it is the highest-value item in the whole sourcing chain.

### A-6 (S2) — Three quote-number generators remain.
Collision is now impossible (unique index added this session), but the read-max-plus-one
allocator should be replaced with the row-locked `LegalDocumentCounters` pattern finance already
uses (`CommercialFinanceApplicationService.cs:1149-1175`).

### A-9 (S3, NEW) — Excluding an unreadable line does not unblock conversion.
Found while building the A-2 gate. `FindConversionBlockers` (`LeadConversionIntelligence.cs:331`)
inspects **every** line on the lead, not just the included ones, so deselecting a zero-quantity
line in the UI still refuses the conversion — the operator must go and correct the lead itself.

Defensible (fix the source, don't hide it), but it is a cliff on an 84-line bid list where one
line is unreadable. **Deliberately not relaxed** as a side effect of this slice: loosening a gate
is a decision, not a by-product. Pinned by
`ConversionWarningGovernancePostgreSqlTests.Excluding_a_zero_quantity_line_does_NOT_bypass_the_lead_level_blocker`
so a future change is intentional.

### A-10 (S2, NEW) — There is no RFQ/RFP classification to gate on.
The Phase 1 gate calls for "only an RFQ or RFP may convert". **That concept does not exist:**
`Lead.InquiryType` is `product|service|mixed` (a BOQ axis, written only by
`ExtractionWorker.cs:1363` and **not written at all** by the email or manual-upload paths, so it
is null for a large share of leads); `Lead.Rfqtype` is free-text `Agreement|Direct` (contract
form); `LeadOccurrenceClassification` is duplicate identity (New/ExactDuplicate/Revision). The
only business-type signal is `EmailTriageOutcome`
(`Ingestion/Triage/EmailTriageDecision.cs:8`) — Inquiry/CommercialNonInquiry/Noise/Uncertain —
which lives on the email ingest row, does not exist for manual uploads, and does not distinguish
RFQ from RFP.

**Deliberately not built.** A new classification field nothing populates would either block every
conversion or default permissive — worse than the honest gap. This needs a product decision on
where the classification comes from before it can be enforced.

### A-7 (S3) — Supplier qualification data absent.
No manufacturer/brand authorization entity and no supplier tier concept anywhere. Phase 4 of the
assignment ranks suppliers by "manufacturer/brand-authorized" — that dimension cannot be computed
from current data.

### A-8 (S3) — No line-participation UI.
`Rfqitem.ParticipationDecision` exists with domain rules, DB constraints and 19 tests, but no
frontend control. The journey step "Mark Selected Lines as Quote" is API-only.

---

## B. External dependencies — Nexora cannot clear these alone

| ID | Blocker | Consequence |
|---|---|---|
| **B-1** | **No web/search provider credential or abstraction.** There is no `IWebSearch` interface and no feature-flag service. `SourcingCaseStatuses.DiscoveryRequired` exists with nothing behind it. | AI external supplier discovery (assignment Phase 5) cannot be built against a live provider. The provider boundary and candidate governance can be built and fixture-tested; live results cannot. **Marked EXTERNAL BLOCKER, not faked.** |
| **B-2** | **No test mailbox or local SMTP sink is running.** | Supplier RFQ send and reply ingestion cannot be exercised end to end. Mitigated in part by C-1 below. |
| **B-3** | **Golden corpus still unmet (A7).** 8 of 9 required document formats have zero real specimens. | Supplier offer extraction cannot be proven against realistic supplier documents. |
| **B-4** | **Live mailbox credentials failing.** The inbound mailbox has thrown `AuthenticationException` every cycle since ≤ 2026-08-05T21:43Z. | Real supplier replies cannot arrive. |

---

## C. Cleared this session

| ID | Was | Now |
|---|---|---|
| **C-2** | **`sales_rep_profiles` had no write path** — the root cause of 44/44 unassigned leads. | `POST api/commercial-intelligence/reps/{userId}/routing-profile` on the controller that already owns `reps`, reusing the existing service, permission convention and manager-role gate. 7 tests. |
| **C-3** | **Extraction warnings were computed and ignored** by the conversion path. | Typed hard/soft gate before any write; soft warnings need a reasoned acknowledgement, hard ones cannot be waived; audited on the existing lifecycle event. 12 real-PostgreSQL tests. |
| **C-1** | **No outbound recipient allow-list or test sink existed anywhere.** `NotificationsOptions` had no such concept. The only thing between a rehearsal and a real supplier's inbox was whichever address sat on the supplier record. **This was a hard prerequisite of the assignment (§2, §12) and it was absent** — the overnight email work could not have been performed safely. | `OutboundEmailGuard` implemented as a decorator over `IEmailSender`, registered as the *only* `IEmailSender` in DI so no transport can bypass it. Four modes: `Live` (default, unchanged behaviour), `AllowListOnly` (fails closed), `Redirect` (rewrites to a sink, clears Cc/Bcc, tags the subject), `DraftOnly`. 15 tests. A real transport with no containment now warns by name at startup. |

---

## D. Pilot limitations to state to the client

- Arabic / Hijri extraction and OCR out of scope (A1, founder-approved 2026-08-06).
- Routing rules are not user-configurable — do not claim configurability.
- Supplier tiers and brand authorization do not exist; supplier ranking uses preferred-supplier,
  purchase history and prior quotations only.
- Quote-stage ATP is a **snapshot, not a reservation**. Correct per BRD, but the client must
  understand a quoted line is not held stock.

---

## E. Production prerequisites

1. **Run the duplicate quote-number audit against production before applying
   `20260806044841_RfqLineParticipationAndQuoteNumberUniqueness`.** The migration includes a
   pre-flight `DO $$` block that names offending `(BusinessUnitID, QuoteNo)` pairs and aborts —
   but it has not yet been run against production data.
2. Governed object storage remains unconfigured (`object_bucket='local'` on 86/86, `/ready`
   Unhealthy).
3. Real malware scanning is still the `Nexora.EICAR` stub; 30 documents are green-labelled
   "Cleared" by a 68-byte test-string matcher.
4. Evidence read-audit still absent.

---

## F. Recommended next slice — one only

> **Surface owner + warning acknowledgement + line participation in the UI, then prove the
> journey in the existing Playwright harness.**

The backend half of the Phase 1 gate is now done and tested. What remains is entirely
presentation and proof: the Convert page must show the resolved owner and the acknowledgement
control, the RFQ workspace must expose Quote/No-Quote per line, and
`Frontend/e2e/core-commercial-journey.spec.ts` — a harness that **already exists**, 29 specs with
auth setup and fixtures — must be extended through convert → mark lines → prepare draft →
double-refresh. That produces the missing `base-journey-browser-result.md` and closes Phase 1.

**Explicitly not next:** external AI discovery (B-1 blocked), reply correlation (A-5 — valuable,
but needs B-2), lineage FK (A-3 — needs the LeadItem projection fixed first), classification
gate (A-10 — needs a product decision, not code).
