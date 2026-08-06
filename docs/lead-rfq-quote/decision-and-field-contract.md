# Lead → RFQ → Customer Quote Draft — Decision and Field Contract

**Phase A output. Written 2026-08-06.** Bounded consultant review, not a new audit.
Panel: RFQ/CRM Technical Sales SME (read-only), SDET/Security/Reliability Auditor (read-only),
Lead Architect (owner of this file).

---

## 1. The headline finding

**The Lead → RFQ → Customer Quote Draft path already exists, end to end, and is wired to the UI.**
The assignment as written is scoped as a *build*. Executing it literally would create a second
conversion pipeline alongside a working one — the exact outcome the assignment's own
anti-spaghetti rule forbids.

| Journey step | Already exists | Anchor |
|---|---|---|
| Lead → RFQ conversion | **Yes**, transactional + idempotent | `Intelligence/Conversion/LeadConversionIntelligence.cs:99` `ConvertAsync`, serializable tx `:107` |
| Conversion endpoint | **Yes** | `POST api/intelligence/leads/{id}/convert` — `ConversionIntelligenceController.cs:55` |
| Conversion UI | **Yes** | `Frontend/src/pages/Intelligence/LeadConvertPage.tsx` — per-line include/exclude `:279`, product override `:319`, qty `:355` |
| RFQ entity, distinct from Lead | **Yes** | `Models/Rfq.cs:6`, `Models/Rfqitem.cs:6`, server-allocated `Rfqno` from `nexora_rfq_number_seq` |
| Customer Quote Draft from RFQ | **Yes**, idempotent | `Services/QuoteService.cs:180` `PrepareDraftFromRfqAsync` |
| Quote Draft endpoint | **Yes** | `POST api/Rfq/{id}/prepare-quote-draft` — `RfqController.cs:103`, requires RFQ:View + Quotations:Create |
| Quote Draft UI hook | **Yes** | `rfqService.prepareQuoteDraft` :195 |
| Customer Quote ≠ Supplier Offer | **Yes**, separate tables, no discriminator | `Quotes`/`QuoteItems` vs `supplier_quotes`/`supplier_quote_lines`; bridge `CustomerQuoteSourcingDecision` (`SupplierQuoteEntities.cs:173`) |

**Decision A1 — the engineering task is REPAIR-AND-PROVE, not BUILD.**
Two capabilities are genuinely absent and are the only sanctioned new construction (§4).

---

## 2. Corrections to the assignment's premises

Recorded because acting on them unreviewed would have caused harm.

| Premise in the assignment | Finding | Consequence |
|---|---|---|
| "Implement one idempotent transactional conversion command" | Exists. Backstopped at the **database**: `UX_RFQ_BusinessUnitID_LeadID` and `UX_Quotes_BusinessUnitID_RFQID`, both unique partial indexes (`Migrations/20260722051308_OperationalizeCommercialLifecycle.cs:304-305`) | Do not write a new command. Data is already safe under concurrency. |
| "Do not add a new RFQ table if the existing domain represents this" | It does. `Rfq` + `Rfqitem` + immutable `CommercialCase` identity | No new RFQ entity. Confirmed. |
| "If no reusable Quote Draft capability exists, do not invent the Quote module" | It exists and is idempotent | No Quote module work. Reuse `PrepareDraftFromRfqAsync`. |
| `QuotationUploaderController` lacks permission attributes | **False.** It carries `Quotations:View` `:29` and `Quotations:Create` `:54` | Retracted. The real gap is `QuoteConfigurationController` (§3). |
| RFQ has an owner to route to | **`Rfq` has no owner column at all** (`Models/Rfq.cs:8-66`); `Lead.AssignTo` is never read by conversion | Ownership dies at the Lead→RFQ boundary. This is a genuine defect, not a config gap. |

---

## 3. The real gaps, ranked

**S1 — ownership does not survive conversion.** `ConvertCoreAsync` never reads `Lead.AssignTo`,
and `Rfq` carries no owner column. Combined with 44/44 production leads `Unassigned`, the
"Named Sales Owner" step has nothing to write to and nothing to read from.

**S1 — validation warnings are decorative.** `ResolveLinesAsync` computes `NeedsAttention` /
`AttentionReason` (`LeadConversionIntelligence.cs:437-455`) including *"Quantity missing"*,
*"No catalog match"* and `uom.NeedsReview` (e.g. `"25 Pack"`). `ConvertCoreAsync` **never reads
`NeedsAttention`** (`:224-296`). The UI colours those lines and sorts them to the top
(`LeadConvertPage.tsx:180-182, :288-296`) but leaves **Create RFQ enabled**. An operator can
convert an RFQ whose quantities the system knows it could not read.

**S1 — no per-line lineage.** `Rfqitem` has **no `LeadItemId` column**. Lines are copied by value
(`:265-296`). The only linkage is `LinkRfqAsync` (`CommercialLineResolutionApplicationService.cs:166-195`),
which *re-guesses* the mapping afterwards: line number, then ProductId, then part identity, then
*"if the counts happen to match, take the first free row"* `:189-190`. That is a heuristic, not
provenance — and it is the audit trail an industrial buyer will ask about.

**S2 — the Quote Draft loses the commercial identity of the part.** `QuoteService.cs:263-280`
carries ProductId, description, quantity, UoM, customer line ref. It **drops** ManufacturerName,
ManufacturerPartNumber, AlternatePartNumber, ItemMaterialCode, Currency, and requested delivery
(`DeliveryLeadTime` hard-set `null` `:277`). A sales engineer prices a line with no manufacturer
and no part number.

**S2 — no RFQ revision axis.** `Rfq` has no revision number; `LifecycleVersion`
(`Models/Rfq.Lifecycle.cs:5`) is an optimistic-concurrency counter that `Quote` then misreads as
an RFQ revision (`Models/Quote.CommercialIdentity.cs:35`). The identity layer already **writes**
`RFQ_REVISION_REQUIRED` impacts (`LeadIdentityApplicationService.cs:868`) and **nothing consumes
them** — only Quote impacts have a resolution path (`QuoteService.cs:1384-1392`).

**S2 — quote numbering: three generators, no unique index.** `QuoteService.cs:292` unlocked
read-max, **not BU-scoped** `:300`; `QuotationUploaderService.cs:295-315` same format, BU-scoped;
`RfqRepository.cs:526` a fourth scheme `QT-{Rfqno}`. `IX_Quotes_QuoteNo` is **not unique**
(`ErpRfqAutomationContext.cs:906`). Serializable isolation makes same-path collisions unlikely by
accident, not by design; cross-path collisions are unprevented.

**S2 — `QuoteConfigurationController` is unauthorized on state change.** `[Authorize]` only;
`:32 POST migrate` iterates **all** BusinessUnitIds `:44` with no tenant check, and `:105 POST`
keeps `request.BusinessUnitId` when the claim is absent or `0` (`:113`).

**S2 — line-level Quote / No-Quote does not exist.** Zero hits for `NoQuote`, `Participation`,
`QuoteReadiness`, `SupplyPath` on any entity. The only bid decision is header-level and untyped:
`Rfq.BiddingDecision`, a nullable `string` (`Models/Rfq.cs:18`). `ConvertRequestItem.Include`
(`ConversionModels.cs:84`) is transient and never persisted.

**S3 — conversion cannot express an unknown closing date.** `FindConversionBlockers:328` hard-requires
`BidClosingDate`, so a genuine open-ended inquiry cannot convert at all. The assignment requires
an explicit-unknown state; there is none, and the BRD forbids inferring end-of-day.

**S3 — classification is never checked.** Nothing reads `Lead.InquiryType`
(`Models/Lead.Inquiry.cs:16`); `lead.Rfqtype` is copied blind `:217`. A non-RFQ that reaches
QUALIFIED converts.

---

## 4. Sanctioned new construction — two items only

Everything else is repair or configuration.

| New component | Why nothing existing can serve | Owner |
|---|---|---|
| `Rfqitem.LeadItemId` (nullable FK) + per-line participation decision (`Pending`/`Quote`/`NoQuote` + reason) | No lineage column and no participation column exist anywhere; the current mapping is a post-hoc heuristic and the only bid field is a header-level free-text string. Both are required by the journey's "Mark Selected Lines as Quote" step. | Implementation Engineer |
| RFQ owner reference + consumption of the existing `RFQ_REVISION_REQUIRED` impact | `Rfq` has no owner column; the revision impacts are already written and silently ignored. Neither can be expressed by an existing field. | Implementation Engineer |

**Rejected as new construction:** new conversion command, new RFQ entity, new Quote subsystem,
new routing engine, new routing tables, new identity model, new state machines for
Intake/Lead/RFQ/Quote, new Platform Admin module.

---

## 5. Field contract — retained vs rejected

**Retained (must be supported by the end of the slice):** the four BRD fields already reported
missing — required delivery date, delivery location, Saudi city/region, closing time — plus one
canonical closing timestamp carrying time zone, source calendar and original source text.
Absent values render as an explicit **"Not Provided"**, never as blank-looking-complete.

**Retained (already present, reuse, do not re-model):** lead identity state
(New/ExactDuplicate/Revision/PossibleMatch), content hash, scan status, occurrence + evidence
links, `Aiconfidence` per line, `CommercialCaseReference` / `NexoraSerial`, tenant scoping via EF
global query filters + Postgres RLS.

**Rejected for this slice:** supplier cost, landed cost, margin, LOA, VAT/ZATCA, sell pricing on
the Lead form; supplier discovery/dispatch/comparison; quote PDF, sending, win/loss.

**Explicitly rejected as a modelling choice:** two independently editable date fields that can
contradict each other. One canonical timestamp + metadata, per §4 of the assignment.

---

## 6. Status mapping — reuse, do not invent

| Concept | Existing representation | Decision |
|---|---|---|
| Quote lifecycle | `SetupMaster` rows, `SetupType="QuoteStatus"`; transitions `DRAFT/SENT/ACCEPTED/REJECTED/EXPIRED/ORDERED` (`QuoteService.cs:1332-1355`) | Reuse `DRAFT`. **`AwaitingInputs` does not exist and will not be created** — a DRAFT with an unmet readiness checklist expresses it. |
| RFQ lifecycle | `RfqstatusId` + `LifecyclePolicy.Canonicalize("Rfq", …)` | Reuse. Do not add header statuses. |
| Lead lifecycle | `LifecyclePolicy.Canonicalize("Lead", …)`, gate `QUALIFIED` | Reuse. |
| Line participation | **none** | New, minimal — see §4. |

---

## 7. Architecture decision

> Reuse `LeadConversionIntelligence.ConvertAsync` and `QuoteService.PrepareDraftFromRfqAsync`
> unchanged in shape. Add gates and lineage *inside* them. Add exactly two new persisted
> concepts (§4). Prove the journey in a real browser against real PostgreSQL. Build no new
> pipeline, entity, engine or module.

Prerequisite cleared 2026-08-06: the working tree is back to `Failed: 0`, `Skipped: 0`, with the
six silently-dropped theory cases restored and a guard added so that class of erosion asserts
rather than hides.
