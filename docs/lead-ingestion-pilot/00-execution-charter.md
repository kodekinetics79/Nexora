# Lead / RFQ Ingestion — Client Pilot Execution Charter

**Owner:** Acting CTO (autonomous execution)
**Started:** 2026-08-06
**Objective:** Bring the Lead/RFQ Ingestion module to controlled Client Pilot readiness for
Tech Connect, and issue an evidence-backed PILOT GO / CONDITIONAL GO / NO-GO decision.

---

## 1. Mission

One reliable, demonstrable journey:

```
Email / Manual Upload / Watched Folder
  -> Ingestion Occurrence -> Source Package -> Classification
  -> Parsing / OCR -> Header + Line Extraction -> Confidence + Evidence
  -> Duplicate / Revision / Possible-Match Decision -> Human Review
  -> Canonical Lead / RFQ -> Numbering -> BU / Sales Engineer Routing
  -> RFQ / Quote readiness
```

**Central requirement:** every RFQ received through an approved pilot channel appears in
Nexora with a visible, explainable, auditable disposition. **Nothing disappears silently.**

This is not aspirational. Every production failure investigated on 2026-08-05/06 was silent
loss wearing a different mask — see §5.

---

## 2. Operating model

Maximum **four concurrent agents**, no overlapping write ownership:

| Role | Ownership |
|---|---|
| Lead Architect / Orchestrator | Sequencing, architecture protection, merge decisions, final call |
| Implementation Engineer | Product code for the locked P0 backlog |
| Security / Reliability Reviewer | Tenant isolation, authz, evidence security, idempotency, recovery |
| SDET / Pilot Auditor | Test matrix, PostgreSQL + browser evidence, attempts to break before GO |

Specialist perspectives are invoked as **short, bounded, read-only reviews** only. No
long-running duplicate contexts. Consultant challenge happens at defined gates; on
disagreement the Acting CTO decides, and the decision is recorded in `03-decision-log.md`.

---

## 3. CTO amendments to the master prompt

Raised before execution, with rationale. Any can be overruled by the founder.

| # | Amendment | Rationale |
|---|---|---|
| A1 | **Arabic / Hijri extraction is OUT of pilot scope** — recorded as an accepted limitation, not silently dropped | Zero Arabic documents exist in production; Tesseract has never executed on a production job; the Arabic language pack is unverified in the container. Open-ended risk that could consume the whole budget. **Founder approved 2026-08-06.** |
| A2 | **Sequence by "what breaks in front of the client," not by FR number** | A full FR-RFQ-01→08 build is roughly a quarter of work. Breadth-first yields a demo that works nowhere. |
| A3 | **Reuse existing numbering; map it to the required format rather than build a parallel scheme** | `NexoraSerial` / `CommercialCaseReference` already generate client-facing references. A second scheme would violate the prompt's own "no duplicate state systems" rule. Revisit only if the client demands the literal `RFQ-KSA-` string. |
| A4 | **Do not claim the 60-second extraction target; measure and publish the real figure** | A real Aramco RFP carries 121 MB of expanded XML. It will not extract in 60s. Publishing an unqualified target puts a false number in front of a client. |
| A5 | **Add cost-per-document to the readiness gates** | Absent from the prompt. On 2026-08-06, 1,133 external AI calls were spent in 3 hours producing zero leads. At 900 inquiries/month unit economics decide whether the pitch is a business. |
| A6 | **Phase 1 audit is time-boxed and runs concurrently with fixing already-proven blockers** | A read-only audit across 8 requirement areas plus a consultant gate before any code change could consume the budget before a line moves. |
| A7 | **Golden corpus depends on real client documents — declared a blocking external dependency on day one** | Synthetic Arabic/scanned documents prove nothing about real extraction. Raised at Phase 0, not discovered at Phase 5. Founder has supplied a corpus; folder path pending. |

---

## 4. Scope boundary

**In:** lead/inquiry/RFQ intake, ingestion channels, source packages, classification,
parsing, OCR, extraction, confidence, human review, identity, duplicate detection, revision
lineage, routing, source evidence, ingestion operations, related security/observability/tests,
and the handoff contract to RFQ/Quote Management.

**Out:** supplier/customer quotation implementation, PO matching, inventory, shipment,
delivery, ZATCA, broad CRM or platform-admin redevelopment, unrelated UI redesign,
microservice decomposition, technology-stack replacement.

The existing React / ASP.NET Core 8 / PostgreSQL architecture is preserved. The BRD's
alternative reference technologies (Kafka, Elasticsearch, Redis, GraphQL) are **not**
adopted merely because they appear there.

---

## 5. Starting position — verified, not assumed

Established by production investigation on 2026-08-05/06. Every item below was confirmed
against live data or executing code, and several overturned earlier assumptions.

### Fixed and deployed in the 24h preceding this charter

| Defect | Evidence | Status |
|---|---|---|
| Quantity silently fabricated (`"1,000"` -> `1`) | 875 of 2,966 line items carried quantity 1 | Fixed: `QuantityParser`, review gate, approve gate, DB CHECK constraints |
| Retry idempotency — every retry refused as duplicate before any call, reported as "All chunks failed" | Job 33: 12 AI calls, all succeeded, job dead-lettered | Fixed: attempt-scoped keys, honest refusal codes, diagnostics into `LastError` |
| Whole document lost to one bad token (`2.0`) or one zero-quantity line | 17 "unparseable output" + 11 "no result" dead-letters | Fixed: tolerant converter, line-level quarantine |
| False "Item count mismatch" on ~100% of documents; silently disabled multi-inquiry splitting | Lead 412: 6 real items, alarm claimed a mismatch | Fixed: check retained only where the expected count is real |
| No UOM canonicalisation on any ingestion path | `each` 2868 / `EA` 19 / `pcs` 3 / `Pcs` 1 / `piece` 1 | Fixed: single shared mapper + canonicaliser with refusal semantics |
| Quote PDF had no unit of measure and replaced the buyer's line references with 1,2,3 | `QuoteService` PDF table | Fixed: "Your Ref" + UOM columns + RFQ reference header |
| Intake rejection reasons never reached the user | `job.reason` received, typed, discarded by the upload page | Fixed: truthful reasons end-to-end |
| Part numbers globally unique across tenants | One collision would abort a client's whole catalogue import | Fixed: unique per `(BusinessUnitId, PartNo)` |
| Genuine 121 MB Aramco RFP rejected as a zip bomb | Recorded reason named the exact size | Fixed: caps sized to real documents + streaming `.docx` reader |
| **1,133 successful AI calls produced zero leads** — duplicate external-dependency ceiling destroyed authorized results at persistence | `AiRequests`: 1,133 Succeeded in 3h; `Leads` created: 0 | Fixed: enforcement stays pre-egress in `AiGovernanceService` |

### Open, carried into this program

- Extraction success rate is **unmeasured**; the clean re-drive that would establish it has
  been blocked repeatedly by the defects above. First honest figure is a Phase 1 deliverable.
- `LeadReviewAudits` has **0 rows** — no human has ever completed a review.
- Tenant provisioning gap: business units 2/4/5/6 have no roles, permissions or statuses.
- `AIConfidence` is a fabricated constant (0.88 on 98.4% of rows) or a field-population
  ratio; it must not be rendered as a percentage to a pilot user.
- Formats never exercised in production: PDF (native or scanned), JPEG/PNG, MSG, EML, HTML.
  **OCR has never executed on a production job.**
- 22 documents whose bytes were lost to pre-disk ephemeral storage; originals recovered and
  hash-verified from the founder's OneDrive, awaiting re-upload.

---

## 6. Definition of done

Per the master prompt §9. GO requires all mandatory gates; CONDITIONAL GO requires no
safety/security/tenant-isolation/data-loss/idempotency blocker and every limitation owned,
mitigated and dated; NO-GO on any silent loss, duplicate corruption, broken revision lineage,
cross-tenant exposure, unrecoverable worker failure, mock-only proof, or open Sev 1/2 defect.

**GO is never issued on test counts alone.**

---

## 7. Documents

| File | Purpose |
|---|---|
| `00-execution-charter.md` | This document |
| `01-repository-map.md` | Shared map — built once, reused by every agent |
| `02-current-state-rtm.md` | FR-RFQ-01..08 requirements traceability |
| `03-decision-log.md` | Decisions, alternatives, consultant challenge, rationale |
| `04-risk-and-blocker-register.md` | Defects, debt, external dependencies, limitations |
| `05-test-and-evidence-matrix.md` | Test ID -> requirement -> evidence -> pass/fail |
| `06-pilot-runbook.md` | Startup, demo sequence, recovery, rollback |
| `07-final-readiness-report.md` | The GO / CONDITIONAL GO / NO-GO decision |
