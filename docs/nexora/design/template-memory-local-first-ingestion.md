# Template Memory — Local-First Lead Ingestion Blueprint

**Status:** Approved design, pending build (post-pilot-deploy)
**Authors:** CTO synthesis of three SME consultations (architecture, AI/document-intelligence, compliance), 2026-08-04
**Decision:** Lead ingestion becomes local-first *by construction*: an external LLM is a bootstrap
accelerator for first-contact document formats only. Repeat formats — the overwhelming majority of a
trading house's volume — extract deterministically, offline, by replaying human-validated templates.

---

## 1. The thesis, corrected

Nexora's customers receive documents from the same 50–200 counterparties in recurring formats. A
generative model treats every document as a stranger; in this segment almost none is. Therefore:

> **An LLM should only ever see a document format once. After that, the system already knows it.**

Two honest corrections from the panel, baked into this design:

1. **The value claim is cost + locality + speed, not autonomy.** Every AI-derived lead is hard-coded
   `NeedsReview` (`ExtractionWorker.cs:759`) and stays that way. Template hits still surface for
   approval until the template earns trust (§5). Template Memory removes LLM calls and external
   egress — not human review.
2. **Deterministic parsing alone cannot carry tables** (.doc tables are flattened today; borderless
   PDF tables resist text-layer recovery). The template — a human-anchored column mapping — carries
   what the parser can't. That is a feature: the part that is hard is exactly the part that learns.

## 2. What already exists (verified, file:line)

| Substrate | Where | State |
|---|---|---|
| Durable extraction queue, SKIP LOCKED, dead-letter, content-hash idempotency | `Extraction/ExtractionQueue.cs` | production-grade |
| Deterministic structured bypass (no LLM) | `ChunkedExtractionService.cs:177-182` | works today |
| Evidence ledger: `DocumentPage`, `DocumentRegion` (bbox + `'Sheet1'!B7`), `FieldEvidence` (field→region + `TransformationsJson`) | `DocumentIntelligence/Persistence/EvidenceLedgerEntities.cs:859-909, 1131-1208` | **spreadsheet path only** — PDF/OCR discard word boxes |
| Header row + per-column header map computed in memory | `NativeSpreadsheetParser.cs:161-162` | computed, then thrown away |
| Human corrections captured: whole-lead Before/AfterJson + per-field change metric | `LeadRepository.cs:1013-1039, 1058-1070`; `Models/Lead.ReviewGovernance.cs:13-27` | **write-only — zero readers** |
| External-provider allow-list, fail-closed | `AI/AiExternalProviderTrust*.cs` | shipped this session |
| Content-addressed immutable documents, SHA-256 verified | `Infrastructure/Storage/` | production-grade |

The learning loop needs a **reader**, not new capture. One capture gap exists (§4.3).

## 3. Tiered pipeline

| Tier | Handles | Engine | Egress |
|---|---|---|---|
| 0 | xlsx/xlsm/xls/csv | existing deterministic parser | never |
| 1 | native-text PDF, .docx, .doc | PdfPig (Apache-2.0) · OpenXML (MIT) · **b2xtranslator** (MIT, .doc→.docx to reach the table reader) | never |
| 2 | scanned/image | Tesseract (already in image; see §7 hazards) | never |
| 3 | **fingerprint matches a template** | deterministic replay, `ProcessingPath=Deterministic` | never |
| 4 | first-contact format | heuristics + review; allow-listed LLM as optional accelerator | only here, only if tenant-authorized |

Insertion points (architect-verified):
- Fingerprint computed in `ProductionDocumentReader.ReadAsync` (~:80-136), emitted on `DocumentExtractionInput`.
- Template short-circuit in `ChunkedExtractionService.ExtractUnstructuredAsync` **before** the
  external gate (:184-198), producing a `CanonicalRfqImportResult` so persistence semantics are unchanged.
- Correction capture hook at `LeadRepository.SubmitLeadReviewAsync`, where the
  `extraction_corrected` metric already fires (:1056-1069).

## 4. The learning machinery

### 4.1 Fingerprints (structural, never positional; similarity bands, never equality)
- **Spreadsheets:** exact key = SHA-256(ordered normalized headers + header row index) for O(1) hits;
  similarity = 0.7·Jaccard(header set) + 0.3·Kendall-tau order agreement.
- **Native PDFs:** label set (normalized short runs / colon-suffixed, tagged to an 8×8 grid) +
  column-peak signature (word left-edge histogram, 1% quantization) + occupancy bitset;
  0.5/0.3/0.2 weighted.
- **OCR:** same shape; labels matched by char-3-gram Jaccard (absorbs 0/O, 1/l), 4×4 grid,
  ≥3 anchor labels mandatory.
- **Apply thresholds:** exact key → apply. Else best ≥0.80 (sheets) / 0.75 (PDF) / 0.70 (OCR)
  **and** margin over runner-up ≥0.10. Ambiguous margin → review with template pre-fill.
- Known failure modes (accepted): email bodies, narrative PDFs, never-repeating long-tail senders.
  **Hit rate is the go/no-go metric — instrument it first.**

### 4.2 Template schema (compliance mandate: structural mappings ONLY, never corrected values)
`ExtractionTemplate` (BU-scoped, EF filter + RLS, unique `(BU, FingerprintExactKey, Version)`):
DocType, FingerprintKind, FingerprintJson, Version, Status(Candidate|Active|Demoted|Retired),
CreatedFromSourceDocumentId, HeaderMappings[] {field, selector(headerNorm/anchor/gridCell — never
literal values), transformChain (reuses `TransformationsJson` vocabulary), validations},
LineTable {columnMap, stopConditions}, Stats {AppliedCount, per-field CorrectedCount, ConsecutiveFailures}.

### 4.3 Correction loop — the one capture gap
Review DTOs carry corrected values but **no source reference** — the system never learns where the
*right* value was. Minimal fix: optional `SourceRef {field, regionId?, sourceAddress?, page?, bbox?}`
on review submit; API infers by searching the parsed grid/text for the corrected value (unique hit →
auto-record), UI asks the reviewer to click the source region only on ambiguity. Persist
`LeadReviewFieldCorrection`. Prerequisite for PDFs: persist `DocumentRegion`s on the PDF/OCR paths
(word boxes exist at parse time today and are discarded).

### 4.4 Poisoning defence (the key rule)
Templates are minted only from `approve` actions (never `save`), always as **Candidate**. A Candidate
never auto-publishes: its next **K=2** matching documents still route to review, pre-filled; it goes
Active only if reviewers approve without editing template-mapped fields. Worst case for one bad
correction: one pre-filled review screen — never a silent wrong extraction.
**Drift demotion at apply time** (all deterministic): item-count conservation, field-shape checks,
anchor-presence, qty×price≈total. Any failure → `ReviewRequired` + counter; 3 consecutive → Demoted.

### 4.5 Empirical confidence (kills the fabricated numbers)
Per (tenant, templateVersion, field): confidence = **Wilson lower bound** of (applications −
corrections)/applications; approve-without-edit is an implicit correct label. UI shows **"verified"**
only when Active ∧ n≥20 ∧ WLB≥0.98; else "needs review". Replaces the `1.0m/0.2m` constants
(`CanonicalRfqNormalizer.cs:189-264`) and the 0.60 gate on LLM self-reports.
**Golden corpus:** every approved review freezes `(contentHash, final values, source refs)` into
`GoldenExample`; a nightly job re-runs the current extractor and publishes field-level exact-match
accuracy per doc class — the marketing-defensible number that does not exist today.

## 5. Trust ladder (review-policy integration)
Template hits remain `NeedsReview` with pre-fill until a template is Active and its fields are
"verified" (§4.5), after which the tenant may opt fields into auto-accept. Autonomy is earned per
tenant per template per field — never asserted.

## 6. Compliance posture (locked)
- Templates are **per-tenant, always** — EF filter + RLS, no shared format library, ever
  (competing trading houses share suppliers; a shared template leaks counterparty relationships and
  negotiated column structure).
- Templates are derived tenant data: deleted on offboarding; lineage template→sourceDoc SHA-256 so
  counterparty erasure requests can reach them. Structural-mappings-only keeps PII out of templates.
- Audit: `TenantGovernanceAuditEvent`, `Area="template-memory"`, `AggregateReference=templateId@version`;
  events TemplateCreated/Versioned/Applied/Demoted/Deleted with evidence payloads per the compliance spec.
- **Approved sales sentence** (until the redaction pre-pass ships, this is the only one):
  *"For document formats your team has already validated, extraction runs entirely inside our
  environment; only for first-contact formats is the document's extracted text — with email addresses
  and phone numbers redacted — sent to a single external endpoint your administrator explicitly
  authorized, with every authorization and call audited and revocable."*
  **Not approved:** "only redacted, minimized content leaves the boundary" (today's redaction is two
  regexes: emails, phones).
- Residency: UAE = DPA transfer clause, not a pilot blocker (DIFC/ADGM and government-adjacent
  excepted). KSA regulated-sector: do not sign on US hosting; commit contractually to a
  Bahrain/KSA region date. Security-questionnaire answer text: see compliance report.

## 7. Phasing

**Phase 0 — hygiene (post-deploy queue, independent of this design):**
EPPlus (`LicenseContext.NonCommercial` in production — live license violation) → ClosedXML/
ExcelDataReader (MIT); FreeSpire.Doc → b2xtranslator; email attachment filter drift (accepts .pptx
which inspection rejects; silently drops .xls/.csv — lost leads); Tesseract engine reuse (engine +
22MB traineddata loaded per document; OOM risk at 512MB).

**Phase 1 — Template Memory for spreadsheets (~1–2 weeks agent work, no new compute):**
`ExtractionTemplate` entity + RLS/filter + drift-guard compliance; spreadsheet fingerprint (inputs
already computed); short-circuit; correction-capture reader at the existing hook; Candidate/Active
lifecycle with K=2 guard; audit events; **hit-rate instrumentation (go/no-go)**.

**Phase 2 — PDF/OCR templates (~2–3 weeks):**
persist regions on PDF/OCR paths; PDF/OCR fingerprints; `SourceRef` capture UI; .doc→.docx
conversion; PdfPig column clustering (ruled/aligned tables only — honest limit).

**Phase 3 — empirical confidence + golden corpus (~1 week):**
Wilson-bound stats, "verified" gating, `GoldenExample` + nightly accuracy job, delete fabricated
confidence constants.

**Phase 4 — post-pilot, requires ≥2GB instance or worker split:**
real redaction/minimization pre-pass (makes the stronger sales sentence true); GBDT field classifier
over token features (trains on hundreds of golden examples, runs in microseconds — the correct first
model); then optionally PaddleOCR det/rec (~20MB, ~1s/page), table-structure models, LayoutLMv3
token classification (~130MB int8). **Explicitly not viable on CPU: generative extraction.**

**Sequencing:** nothing here starts before the pilot deploy (`release → main`) is out and verified.
Phase 1 touches the extraction hot path and gets its own adversarial review cycle.

## 8. Why this wins
- **Independence:** repeat formats extract with zero external calls — not because policy blocks
  them, but because nothing needs them. Marginal cost per repeat document ≈ 0, forever.
- **Self-learning that survives a security review:** no shared model, no cross-tenant learning,
  deterministic replay, every mapping auditable to the correcting human ("this field maps this way
  because Fatima corrected it on 12 Aug").
- **Converts two audit findings into features:** fabricated confidence → measured confidence;
  missing corpus → self-assembling corpus with a published accuracy number.
- **Positioning:** "Nexora learns your counterparties' paperwork" — per-tenant compounding advantage
  that generic cloud OCR/LLM competitors structurally cannot copy.
