# Current State and RTM — Checkpoint

**Checkpoint taken:** 2026-08-06
**Scope:** FR-RFQ-01 .. FR-RFQ-08, condensed. The long-form evidence stays in
`02-current-state-rtm.md` (903 lines) and `04-risk-and-blocker-register.md` (929 lines);
this file is the summary view and does not restate them.

> **Reading rule.** Status is scored against **committed and deployed** code. A fix that
> exists only in the uncommitted working tree is marked `[WT]` and does **not** upgrade a
> status. 42 working-tree entries are uncommitted at this checkpoint (§ *Repository state*).

---

## 1. Requirement status

| ID | Requirement | Status | Primary evidence | Remaining pilot blocker |
|---|---|---|---|---|
| **FR-RFQ-01** | Ingestion channels (mailbox, manual upload, watched folder) | **DEFECTIVE** | Manual upload VERIFIED — 57 production jobs, 22 leads, multi-file batches. Mailbox failing `MailKit.Security.AuthenticationException` every cycle since ≤ 2026-08-05T21:43Z while logging *"Email fetch completed successfully."* Watched folder implemented + scheduled, never executed (no tenant directory). SFTP/SharePoint absent. | Mailbox credentials are invalid **and** the failure is invisible. `[WT]` poll ledger + `EmailPollerHealth` written but not committed, not deployed, not proven against a real mailbox. |
| **FR-RFQ-02** | Format support (PDF native/scanned, DOCX, XLSX, HTML, JPEG, PNG, MSG, EML) | **PARTIAL** | Production census: `.doc` 253 occ / 25 jobs succeeded, `.xls` 28/3, `.docx` 7/1, `.txt` 1/1, `.csv` 1/1. Native-before-OCR ordering correct (`ProductionDocumentReader.cs:371-386`). `[WT]` `.msg`/`.eml`/`.html` readers + allow-list entries added. | **Two blockers.** (a) Tesseract native binding has never been exercised in production — no `TesseractEngine` has ever been constructed there; if the bind fails, every scanned PDF and image returns empty with `OcrStatus='Failed'`. `[WT]` `OcrEngineStartupProbe` exists but is undeployed. (b) **Golden-corpus dependency A7 unmet**: zero real specimens for 8 of 9 required formats. |
| **FR-RFQ-03** | RFQ numbering | **PARTIAL** | 51 leads → 51 distinct `NXR-2026-0000NN`, sequence contiguous, **0 collisions** under a concurrent 15-attempt re-drive. | Not a pilot blocker. Sequence is global, not per-`(tenant, year)` (D-03.1) — latent until a second tenant is live. `RFQ-KSA` prefix is one config row. Agreement/contract reference (D-03.2) does not exist; scope-gated. |
| **FR-RFQ-04** | Extraction fields, header + line | **PARTIAL** | 46 leads, 46/46 carry ≥1 item, 3,121 line items. Manufacturer 90%, part number 93/95%, `ItemText` 97%. | **Four BRD-required fields captured nowhere**: delivery location (0), Saudi region/city (0), required delivery date (0 of 3,121), closing time-of-day (0 of 46 — every `BidClosingDate` is midnight). `HeaderRemarks` is a 1–2 sentence summary of contractually binding text and is also used as a `[NEEDS REVIEW]` diagnostics channel. |
| **FR-RFQ-05** | Amendments become versions, never new RFQs | **DEFECTIVE** | `LeadIdentityApplicationService.cs:165-166` — a possible match is raised **only when customer identity is unresolved**. Both scopes resolving (the healthy case) + no `LogicalGroupKey` (0/47) ⇒ a 1.00-scoring byte-identical amendment falls through to `:184-197` and mints a new `Lead` + `LeadRevision` #1 + `CommercialCaseReference`. | **Yes — P0.** Revision matching is additionally capped at the 250 newest leads (~8 days at 900 inquiries/month), and all 23 Legacy-backfilled leads have `CustomerScopeKey` NULL so they are unreachable by the unbounded path. Untouched in the working tree. |
| **FR-RFQ-06** | Duplicate / revision / new decisions with human review | **PARTIAL** | `LeadMatchCandidates` = 0 rows: three of four decisions have never been produced from a real document. Production data is **not** corrupted today. | The one action that would demonstrate the requirement — confirming a revision in the match-review queue — was the action that overwrote the canonical lead with normalised hash text (`Rfqno` → `rfq20260012`) and deleted every `LeadItem` field except five. `[WT]` `ProposedLeadSnapshotJson` (jsonb) + `ApplyVerbatimProjection` address this; **uncommitted, and the queue has still never been driven end-to-end**. |
| **FR-RFQ-07** | Route accepted RFQs to a Sales Engineer / review queue | **DEFECTIVE** | **44 of 44 auto-decisions = `NO_MATCH_EVIDENCE` / `Unassigned`.** `sales_rep_profiles` = 0, `customer_ownerships` = 0, `Leads.CustomerID` = 0/51. The 7 assigned rows are May-2026 `MIGRATED_ASSIGNMENT` backfill. Re-verified at this checkpoint: **no controller exposes a sales-rep profile write path**; `grep -rn "commercial-routing" Frontend/src` = **0 hits**. | **Yes — the journey's terminal blocker.** Review queue (FR-RFQ-07.2) works: 41 Open items with `ReasonCode` and a 4 h SLA — nothing is lost. But no lead can reach a named owner, so *"Assign to Sales Queue → Ready for Quotation"* cannot be demonstrated. Untouched in the working tree. |
| **FR-RFQ-08** | Source evidence retained, governed, audited | **PARTIAL** | Immutability VERIFIED (`…/sha256/{digest}.{ext}`, `object_version = content_hash` on 86/86). Hash VERIFIED both providers. Link to RFQ + occurrence VERIFIED, 0 orphans. Metadata 289/289. | **Governed storage MISSING** — `object_bucket='local'` on 86/86, `/ready` = `Unhealthy`. **Access-audit history MISSING** — nothing records who read a document. **Retention unenforced** — full module + operator API, no hosted sweeper, `evidence_retention_policies` = 0 rows. Scan status **DEFECTIVE** — `BuiltIn` provider is an EICAR string matcher; 30 real customer documents are green-labelled "Cleared", 54 still Quarantined. 45 of 86 documents have been unreachable at least once. |

**Verified: 0 of 8. Partial: 5. Defective: 3. Missing: 0.**

---

## 2. Repository state at checkpoint

Two independent repositories exist under `/Users/zackkhan/Nexora`. The pilot program touches
exactly one of them.

| Repo | Branch | State | Relevance |
|---|---|---|---|
| `Nexora-main` | `release/nexora-v2-v3-accelerated`, **11 commits ahead** of origin | 25 modified, 17 untracked (**42 entries**), +2,451 / −191 on tracked files | **This program.** All working-tree changes dated 2026-08-05 22:00–23:40. |
| `RFQ-Automation-Vite` | `main`, diverged 14 / 174 | 5 modified, 3 untracked | **Unrelated.** All files last written **2026-07-08**. Pre-existing, not this program's work. Left untouched. |

`Nexora-main/Frontend` has **zero** modifications. Every working-tree change is backend.

### Two migrations are staged but unapplied to production

| Migration | Operations |
|---|---|
| `20260806025029_EmailPollLedgerAndSkippedAttachments` | `EmailIngests.SkippedAttachmentsJson` varchar(2000); `Email_Configurations.{ConsecutivePollFailures int NOT NULL default 0, LastPollAttemptOn, LastPollError varchar(500), LastSuccessfulPollOn}` |
| `20260806025406_RetainVerbatimMatchCandidateSnapshot` | `LeadMatchCandidates.ProposedLeadSnapshotJson` jsonb NULL |

Both have `Down()` methods. Neither has been applied to the Neon production database.

---

## 3. Accepted limitations carried forward

| ID | Limitation | Basis |
|---|---|---|
| A1 | Arabic / Hijri extraction and OCR out of pilot scope — `tessdata/` holds `eng.traineddata` only; `Dockerfile:16` installs no `tesseract-ocr-ara` | Founder approved 2026-08-06, `03-decision-log.md` |
| A7 | Golden corpus incomplete — 8 of 9 required formats have zero real specimens; the 58 `.csv` are synthetic fixtures, the 1 `.pdf` is a 45-byte 0-page stub | Declared blocking dependency; unmet |
| — | Routing rules are not user-configurable (`Program.cs:282` `AddSingleton(new RoutingPolicy())`, no rules table, no CRUD, no UI) | Do not claim configurability to the client |
