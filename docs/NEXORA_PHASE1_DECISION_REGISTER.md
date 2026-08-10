# Nexora Phase 1 — Decision Register

**Status: OPEN. Engineering must not guess any item on this page.**

Every row is a business decision owned by the product owner (with Tech Connect Finance,
Compliance or IT where noted). Under the BRD v3.0 control instruction, where the extract
records an ambiguity, engineering must stop and ask rather than invent an answer.

---

## RATIFIED — 2026-08-09, product owner

Six blocking decisions are closed. These are now binding on engineering and supersede any
earlier guidance, including `AGENTS.md` where noted.

### R1 · ZATCA moves to LAST, capability first, credentials later — closes E29, D5

**Decision:** build the ZATCA capability, and obtain Tech Connect's production credentials
afterwards. ZATCA development is sequenced **last**, not as a parallel track.

**Consequence:** the Gate P-A parallel track is cancelled. Gate 7 keeps the ZATCA build.

**Engineering note (recommended approach):** build and certify the full pipeline — UBL 2.1
serialization, invoice hash chaining, XAdES signing, TLV QR — against the **ZATCA sandbox**,
which does not require the client's production credentials. That way the capability is proven
and testable long before Tech Connect supplies anything, and the only remaining step is
swapping in the production CSID. Do not let "credentials later" become "unproven at go-live".

**Open risk to accept:** pilot acceptance requires at least one compliant e-invoice. Until this
gate completes, that criterion is unmet — which is now an accepted, scheduled position rather
than an unknown.

### R2 · Client-hosted deployment; client provides storage — closes E38, and supersedes the S3 rule

**Decision:** the client hosts the application and provides storage space, avoiding S3. Final
confirmation rests with the client.

**Consequence:** data residency (PDPL) is solved by hosting inside the client's KSA
infrastructure. This **supersedes** the `AGENTS.md` rule that pilot readiness requires
S3-compatible evidence storage and treats local storage as development-only.

**Engineering note (recommended approach):** do **not** rewrite the storage layer to write
directly to a filesystem. Deploy **MinIO** (S3-compatible, runs on the client's own hardware)
inside the client environment. The client gets no cloud bill and full residency; we keep the
existing content-addressed, hash-verified, immutable object-storage interface that is already
built and tested. Rewriting to raw disk would discard working integrity guarantees and is the
more expensive path, not the cheaper one.

**Now in scope as a result:** a deployable on-premise artifact and install runbook; the backup
obligation shifts to the client and must be written into the deployment requirements, not
assumed.

### R3 · Both shipment directions exist as distinct concepts — closes E19

**Decision:** inbound for inventory, outbound for customer delivery. They are two things.

**Consequence:** create a new **inbound supplier shipment** aggregate keyed to the Supplier PO,
carrying the six FR-MAS milestones and the Saudi port master. The existing `Shipment` entity
remains the **outbound** customer despatch and keeps its link to the Sales Order. Neither is
re-scoped into the other.

### R4 · One authoritative supplier PO, classified by demand source — closes E5, E6, E7

**Decision:** the customer PO is uploaded and mapped to its RFQ and Quote. The supplier PO is
also mapped, and is classified as either **stock replenishment** or **specific customer
requirement**. When it is a customer requirement it carries the RFQ, Quote and Customer PO
references so the chain is traceable end to end.

**Consequence — this repairs the broken spine:**

- `SupplierPurchaseOrder` is **authoritative**. `ProcurementHandoff` is demoted to an external
  integration mirror and must not be a second source of truth.
- Add a demand-source discriminator: `STOCK` | `CUSTOMER_DEMAND`.
- For `CUSTOMER_DEMAND`, add and enforce references to Quote, Customer PO and Sales Order
  alongside the existing RFQ reference. For `STOCK`, those are legitimately absent — which is
  exactly why a discriminator is required rather than plain nullable columns.
- `CustomerPurchaseOrder` gains its Quote and RFQ references and a source-document link.

### R5 · Price provenance attestation before send — closes E9, replaces the LOA gate

**Decision:** pricing originates from the Sales Lead or the client's own system. Before a quote
is sent, the sales rep is asked to confirm the price source — from their sales manager, or from
a supplier quote — so that a mistake can be caught before it reaches the customer.

**Consequence:** this replaces the BRD's formal LOA approver workflow (FR-QTM-04) with a
lighter provenance-attestation gate. Recorded as an **approved deviation** from FR-QTM-04.

**Engineering note — how this must be built to be worth anything:**

- The gate is **server-enforced**. The send endpoint refuses a quote with no matching
  attestation. A dialog that only exists in the browser is decoration.
- The attestation is **persisted**: who confirmed, when, the declared source, the specific
  supplier quote or manager reference, and the exact line prices at the moment of confirmation.
- The attestation is **invalidated by any price change** after it is given, forcing
  re-confirmation. Otherwise a rep confirms, edits the price, and sends unchecked.
- This also fixes the existing fail-open defect: the current below-floor guard never blocks an
  item with no price history, whereas attestation is required on **every** send.

### R6 · Arabic, RTL and Hijri UI deferred to a future version — closes E30

**Decision:** not a blocker for Phase 1. Deferred beyond this release.

**Consequence:** the Gate P-B parallel track is cancelled and Gate 9 shrinks substantially.
Recorded as an **approved deviation** from the bilingual requirements in FR-RFQ-04, FR-QTM-01,
FR-QTM-06, FR-DLM-01 and the localization NFR. The application stays English-only, and no
bilingual claim may be made in any demo or document.

**One carve-out engineering recommends keeping in scope — please confirm or reject:** Hijri
*date handling* is a data-correctness problem, not a language problem. Saudi government tenders
on Etimad publish closing dates and times in Hijri, and the system currently discards the
closing **time** entirely and has no Hijri parsing at all. An English-only interface can still
parse, store and display a correctly converted Hijri deadline. **Getting this wrong loses
bids.** The cost is small and it sits inside Gate 1; the cost of adding it after go-live is not.

---

Two classes of item appear here:

- **D-series** — carried from BRD v3.0 §9. The BRD itself does not settle these.
- **E-series** — discovered during the 2026-08-08 repository audit. These are places where
  the code has *already made a silent choice* that the BRD never authorized. These are the
  urgent ones: every day they stay open, more code is written on top of an unratified
  assumption.

### R7 · Quote validity is the user's, and changes are reasoned and logged — supersedes E11

**Decision:** the user sets the validity/expiry on a quote. If they change it afterwards they give
a reason, and that reason is **logged and visible**, because a buyer revising a closing date for
budget or any other reason is normal and the customer needs to see what happened and why.

**Consequence:** this replaces the engineering proposal to gate auto-expiry by lead source, and it
is the better answer — it generalises instead of special-casing tenders. Automatic expiry still
runs against the *current* validity, so it stops fighting the user rather than being switched off.

**Why it matters:** FR-QTM-07 as written expires a quote on our own validity date. Tender bid
validity is set by the BUYER — commonly 90–120 days and extendable on request — so a rule that
expires on our date would mark live Etimad bids Expired while evaluation was still running, and
the pipeline would vanish from the dashboard the pilot is judged on.

**Engineering note:** the change must be an auditable event, not a silent field update. A reason
nobody can read later is not a reason.

### R8 · The cost model stays simple: landed price, margin, tax — supersedes E25, E26, E27

**Decision:** the customer sets the price in Nexora and that is treated as the full and final sale
price. The cost model is deliberately shallow: **(1) landed price, (2) margin, (3) tax**, plus at
most one or two further variables if genuinely needed. Additionally, **open an integration option
to import product prices from the client's existing ERP**.

**Explicitly NOT built:** a customs-duty rate engine driven by HS code, Incoterm cost derivation,
SASO/SABER as a computed cost component. Engineering had recommended these; the product owner
overruled, and the reasoning is sound and worth preserving:

> *"making it more complex could be easier for you but we have to think about maintenance as well
> that will be done by human engineers — the more complex the harder for them"*

A sophisticated model the maintaining engineers cannot reason about is worse than a shallow one
they can. Because the entered price is authoritative rather than computed, duty is already inside
it and modelling duty separately would double-count or contradict it.

**Consequence:** `DutyCost` staying at zero is no longer a defect — it is consistent with a model
where the price is stated, not derived. If that ever changes, this decision must be revisited
first.

### R9 · Bid bonds and manufacturer authorisation letters are OUT of scope

**Decision:** surface/highlight them to the customer if useful, but Nexora does **not** process
them. No bond register, no bank-guarantee facility tracking, no agency-letter workflow.

**Recorded risk, accepted:** these decide bid *eligibility* — a missed or short-dated bid bond is
automatic disqualification regardless of price, and a KSA utility tender typically demands an OEM
authorisation letter naming the tender. Tech Connect keeps that process outside Nexora. Written
down so nobody later mistakes the absence for an oversight.

---

## D-series — carried from the BRD

| ID | Decision | Blocks | Owner |
|---|---|---|---|
| D1 | BRD v3.0 is *Draft for Review* with all five approver signatures blank. Do we build against it as-is? | All gates | Product owner + Tech Connect |
| D2 | Tech Connect must supply the in-scope customer/BP list, vendor master and product/purchase-history data. | Gate 10 | Tech Connect |
| D3 | Future ERP platform and live integration contract are not approved. | Gate 7, Phase 2 | Tech Connect IT |
| D4 | Carrier/freight-forwarder subset for Phase 1 is not identified. | Gate 5 | Product owner |
| D5 | ZATCA-versus-ERP statutory and accounting authority boundary. | **Gate 7 — start now** | Finance + Compliance + IT |
| D6 | "Immutable RFQ source" vs. retention/deletion policy: authorized disposal and legal-hold rules. | Gate 1, Gate 9 | Compliance |
| D7 | Payment-collection narrative is broader than the requirements, which define reminders but not an AR engine. | Gate 8 | Finance |
| D8 | WhatsApp appears in narrative text but not in the atomic ingestion requirement. | Gate 1, Gate 7 | Product owner |
| D9 | Architecture section is a recommendation, not a mandate. Confirm the existing .NET stack stays. | All gates | Product owner (**recommend: confirm stack stays**) |
| D10 | Capacity, concurrency, file size, OCR/Arabic accuracy, report SLA and defect severity are unmeasurable as written. | Gate 9 | Product owner |
| D11 | "Average RFQ response time improvement" needs an approved baseline and target. | Gate 10 | Sales + Tech Connect |
| D12 | TSDM requires final definition with Tech Connect. | Scope | Product owner |

---

## E-series — silent choices the code has already made

These were found by auditing the repository against the 81 requirements. Each one is a
divergence between what the BRD says and what the code does. **Ratify or correct — do not
leave open.**

### Identity and numbering

| ID | Finding | Decision needed |
|---|---|---|
| E1 | **RFQ IDs are `NXR-{YYYY}-{NNNNNN}`, not `RFQ-KSA-{YYYY}-{sequence}`** (FR-RFQ-03). Zero occurrences of `RFQ-KSA` in the repo. The prefix is configurable but has no country token and no admin UI. | Accept `NXR-` as an approved deviation, or renumber? If renumber: retro-fit existing records, or cut over from a date? This identifier is already embedded in commercial-case references and printed on downstream documents — the cost rises every week. |
| E2 | **No agreement/contract reference entity** for standing RFQ agreements (FR-RFQ-03). Only a free-text field. | Is a frame-contract entity in Phase 1, or is free text acceptable for pilot? |
| E3 | **"Response ID" (FR-QTM-06) has no counterpart in the model.** | Which identifier is this — supplier solicitation, quote revision, customer portal response reference, or the Nexora Serial? |

### The authoritative spine

| ID | Finding | Decision needed |
|---|---|---|
| E4 | **`Order` is both the Sales Order and a legacy manual order.** `OrderSourceTypes.Manual` permits an order with `CustomerAwardId`, `QuoteId` and `RfqId` all null — an order with no RFQ lineage at all. | Does Phase 1 accept a Sales Order with no lineage, or is `SourceType=MANUAL` retired/hidden for the pilot? FR-COM-07 says the Sales Order is the single source of truth; today it can be an orphan. |
| E5 | **Two competing supplier-PO models exist:** `SupplierPurchaseOrder` (internal, RFQ-keyed) and `ProcurementHandoff` (external-ERP, Sales-Order-keyed, holding `ExternalSupplierPoNumber` as a *string* with no FK). They are not joined. | Which is authoritative? The answer determines where the missing `CustomerPurchaseOrderId`/`OrderId` FKs and the Incoterm/HS-code fields land. Cannot proceed on Gate 4 without this. |
| E6 | **`SupplierPurchaseOrder` has no link to the Customer PO or Sales Order** (FR-SPO-05). It carries `RfqId` only, so every downstream trace re-joins through the RFQ. In practice **the RFQ, not the Sales Order, is the de-facto spine** — the inverse of FR-COM-07. | Ratify the RFQ-centred spine and amend the BRD, or add the FKs and make the Sales Order authoritative as written? |
| E7 | **`CustomerPurchaseOrder` has no `QuoteId`, no `RfqId` and no source-document reference.** It reaches the quote only via `CustomerAward`. There is nowhere to store an uploaded PO document. | Confirm the intended key structure before FR-COM-01 (PO upload) is built, since the document link needs a home. |
| E8 | **Two duplicate-detection engines run on the same ingest with different rules**, writing to two different review screens. One holds the record before creation (BRD-correct); the other creates the record and then flags it (BRD-forbidden). | Which engine is authoritative? And is "record must not be created" literal, or is a quarantined row acceptable? |

### Commercial control

| ID | Finding | Decision needed |
|---|---|---|
| E9 | **There is no LOA approval gate** (FR-QTM-04). Zero occurrences of `LOA`/`ApprovedMargin`. The only pre-release control is a below-floor guard that **fails open by design**: an item with no price history has no floor, so it never blocks. A first-time item can be quoted and sent with no approver, no date and no recorded margin. | Define the LOA authority matrix: who approves, threshold basis (margin %, absolute value, below-floor delta), per-line or per-quote, and whether an approved LOA survives a revision. **This is the largest commercial-control gap in the product.** |
| E10 | **Price-discrepancy tolerance is exact decimal equality** (FR-COM-04) — not ±2%, not even a hardcoded literal. Rounding noise will flag as a discrepancy. Worse, **the PO-entry UI pre-fills price and quantity from the quote line**, so the default state is always "no discrepancy" — the discrepancy engine is validating the system against itself. | Set the tolerance value, its storage scope (tenant / customer / price-list), whether an absolute floor applies alongside the percentage, a separate quantity tolerance, who may override, and whether override needs a reason code. |
| E11 | **Quote auto-expiry does not match the BRD** (FR-QTM-07). Code expires 14 days *after* validity (configurable 1–365, single knob). The BRD requires expiry *on* the validity date **or** 90 days from submission with no response. The 90-day rule does not exist. | Confirm both triggers are required and whether 90 days is fixed or configurable. |
| E12 | **Supplier weighted scoring does not exist** (FR-QTM-03). Selection is a hardcoded sort by landed cost, then coverage, then lead time. Warranty and payment terms are captured upstream but dropped from the comparison contract. | Define the weight set, its scope, who may edit it, whether changes are versioned, how free-text warranty is quantified, and whether the weighted score *overrides* or merely *annotates* the existing lowest-cost recommendation. |
| E13 | **Supplier tiers (Tier 1/2/3) do not exist as a field** (FR-QTM-01, FR-MDM-03). Tier-targeted dispatch is currently unimplementable. | Define what determines a tier (contract status, spend band, governance status), where it is mastered, and whether tier gates dispatch eligibility or only ordering. |

### Inventory and availability

| ID | Finding | Decision needed |
|---|---|---|
| E14 | **ATP omits incoming shipments.** Implemented ATP is `onHand − reserved − allocated − quarantine − damaged − expired − safetyStock`. Incoming is returned as a separate column. FR-INV-02 says ATP = on-hand + incoming − reservations. The code is arguably *better* commercial practice (a promise backed by units in the building) but it is a silent deviation. | Adopt physical ATP with a separate `ProjectedAvailable`, or change the code to match the BRD? One of the two documents must move. |
| E15 | **ATP is not exposed during quotation preparation.** The quote create/edit screens never call availability — the BRD's explicit clause. A rep can commit a delivery date with no stock signal. | Confirm this is Gate 6 scope. (This is the cheapest high-value fix in the audit.) |
| E16 | **Lot/batch/serial tracking does not exist.** `Inventory` has `BatchTracking`/`SerialTracking` booleans with **no entity behind them** — the toggle exists, the tracking does not. | Full lot master with lot-level balances and lot-costed movements, or lot-as-attribute on goods receipt and movements? The latter satisfies traceability at a fraction of the cost but not "maintain quantities by lot." |
| E17 | **Legacy `Product.QtyOnHand` is still written by some paths**, bypassing the advisory lock and the movement ledger. The reconciliation endpoint only reconciles `Inventory.QtyOnHand`, so this drift is undetectable. | Agree a removal date, a CI guard, and whether reconciliation becomes a scheduled worker with an alert on non-zero drift. |
| E18 | **No max/upper stock bound exists** (FR-INV-04 says min/max). Only `ReorderPoint`. No alert job reads it. | Confirm min/max both required, and the alert channel (email digest vs. a follow-up task). |

### Logistics, delivery, tax

| ID | Finding | Decision needed |
|---|---|---|
| E19 | **`Shipment` is an outbound sales despatch keyed to the sales `Order`.** FR-MAS describes *inbound* supply from a supplier PO to a Saudi port. Building inbound milestones on the existing entity models the wrong direction. | New inbound `SupplierShipment` aggregate keyed to `SupplierPurchaseOrder`, or re-scope the existing entity? These are incompatible designs. **Engineering must not choose this one.** |
| E20 | **Shipment status is ungoverned free text** from an unseeded picklist. Every tenant invents its own milestones, so no cross-tenant KPI, no delay rule and no audit-grade milestone evidence is possible. | Approve the six BRD milestones as a server-enforced domain enum with transition validation. |
| E21 | **Named Saudi port master does not exist** (FR-MAS-01). | Who supplies the controlled list, and is it a governed master-data type (audited, bulk-importable) or a simple picklist? |
| E22 | **Customs-clearance and putaway lead times have no source of truth** (FR-MAS-03). | Per port, per supplier, per item, or a single tenant default? |
| E23 | **SMS and WhatsApp have no provider and no contract** (FR-DLM-06). Only email is wired. | Which provider (Unifonic / Twilio / WhatsApp Business API), who owns KSA sender-ID registration, and is email-only an acceptable Phase 1 reduction? |
| E24 | **The only user-visible "invoice" hardcodes a UK tax ID (`GB123456789`), 10% VAT and `$` currency** — in a product whose primary market is KSA, where 15% VAT is law. It does not call the governed backend invoice at all. | Approve immediate removal or replacement of this template. It is a live misrepresentation risk in any demo. |
| E25 | **VAT is an operator-typed amount.** A `Taxis` table with rate/country/effective-date exists but **no resolver consumes it**; the code carries a standing `TODO` acknowledging this. There is no 15% constant. | Is 15% a KSA constant for Phase 1, or must `Taxis` drive per-GCC rates (UAE 5%, zero-rated exports, reverse charge)? Is VAT computed on the landed base including duty and freight? |
| E26 | **SASO/SABER does not exist anywhere** (FR-QTM-05, FR-MTR-02). | Per-shipment fee, per-certificate fee, or per-line percentage? Who supplies the rate, and is it conditional on HS code or product category? |
| E27 | **Landed cost is supplier-internal and never itemized on the customer quotation.** The PDF prints subtotal, discounts, tax, total — no duty, no freight, no conformity line. FR-QTM-05 requires itemization. | Confirm the required itemization layout on the customer-facing document. |
| E28 | **Standard vs simplified invoice.** Government and semi-government customers (FR-CST-01) imply standard/clearance (pre-issuance), the stricter ZATCA path. | Which path is Phase 1? This determines the entire integration shape and must be settled before any Gate 7 code. |
| E29 | **ZATCA onboarding status is unknown.** Does Tech Connect already hold a ZATCA EGS onboarding OTP and production CSID, or must Nexora onboard from scratch via the sandbox? | External lead time — **this is the single longest pole in the pilot and cannot be compressed by adding engineers.** |

### Localization and follow-up

| ID | Finding | Decision needed |
|---|---|---|
| E30 | **The app is deliberately locked to English** (`lng: 'en'`, switcher hidden) because page bodies are hardcoded English. An `ar` resource bundle exists but no RTL handling exists anywhere. Arabic OCR is absent — only `eng.traineddata` ships and the engine is pinned to `"eng"` in three places. | Define "bilingual": translated commercial documents, or bilingual labels over Latin data? Dual-column single PDF or two documents? Which Arabic font is licensed for embedding? Full bilingual extraction or Arabic-recognised-then-translated? |
| E31 | **Hijri does not exist anywhere**, and RFQ closing *time* is discarded (the extraction prompt forces `YYYY-MM-DD`). Saudi government tenders publish Hijri closing datetimes; a missed closing time is a lost bid. | Store both calendars as first-class fields or derive Hijri for display? Which authority (Umm al-Qura)? Which is legally binding, and in which timezone is closing time recorded? |
| E32 | **Escalation math is calendar-based, not business-day.** Zero `DayOfWeek`/holiday logic exists. In KSA the weekend is Friday–Saturday, so every SLA will mis-count and escalations will fire into the weekend. | Confirm the KSA Fri–Sat weekend and which holiday calendar is authoritative, and whether it is per-tenant. |
| E33 | **"My Follow-ups" is quote-only and fragmented.** Follow-up tasks have exactly one producer in the entire codebase (quote send). Alongside it sit four `/today` pages, `/sales/actions` and three `/copilot` routes — six disconnected lists where the BRD asks for one. | Confirm the single consolidated worklist is Gate 8 scope and which of the six surfaces are retired or hidden. |
| E34 | **Follow-up rules are tenant-global only.** No customer-account or product-category scoping exists (FR-SBF-04). | Is per-account/per-category override in Gate 8 scope, or is tenant-global accepted for pilot with a documented deferral? |
| E35 | **Only 1 of the 5 required reminder triggers exists** (RFQ approaching closure). Missing: Quote/No-Quote decision, supplier response overdue, shipment ETA, invoice payment due. | Which are pilot-mandatory? Supplier-response-overdue and invoice-due have no upstream data plumbing at all. |

### Platform, security and hosting (non-functional)

| ID | Finding | Decision needed |
|---|---|---|
| E38 | **Saudi PDPL data residency is not met and cannot be met on the current stack.** `render.yaml` sets no `region` (defaults to US-Oregon), `vercel.json` sets none, and the Neon region is undocumented. **None of Render, Vercel or Neon offers a Saudi region.** | Accept US/EU residency with an approved PDPL transfer mechanism, or fund re-platforming to a KSA-resident host (STC Cloud, Oracle Jeddah, AWS Bahrain)? **This is an infrastructure programme, not a sprint, and it blocks Gate 9.** |
| E39 | **MFA does not exist for customer logins.** Tenant login is email + password → JWT. The only MFA in the entire codebase is TOTP protecting *our own* SaaS operator console. The BRD requires MFA via Azure AD or equivalent for **every** login. | Will Tech Connect supply an Azure AD/Entra tenant for OIDC federation, or must we build first-party TOTP for tenant users? |
| E40 | **Real malware scanning is switched off.** ClamAV is intentionally not deployed; the active scanner is a structural inspector that, in its own words, "detects no real malware" and logs a reduced-security-posture warning on every boot. Enabling it costs ~$85/month. | Approve the spend and enable before pilot. For a product whose primary input is customer-supplied documents, this is the cheapest material risk reduction available. |
| E41 | **99.5% availability is structurally unreachable.** A single Render instance with no `plan` or `numInstances` means every restart and deploy is downtime. | Approve multi-instance hosting and a communicated maintenance window, or renegotiate the availability target. |
| E42 | **No backup exists for the document estate.** The 5 GB disk holding every source document has no backup configured, and the config file itself states that losing the volume is unrecoverable data loss. There is no restore runbook and no tested restore against RPO ≤ 24h / RTO ≤ 8h. | Assign an owner for backup/restore and schedule a **tested** restore. This is the single largest unmanaged risk in the deployment today. |
| E43 | **HTTPS redirection is commented out** and the database connection string uses `Trust Server Certificate=True`, which disables certificate validation. TLS is entirely delegated to the hosting edge. | Enable redirection and HSTS at the app, and confirm the certificate-validation posture for the database connection. |
| E44 | **Master-data changes are not before/after audited.** The audit infrastructure supports before/after values, but the Customer, Supplier and Product controllers — the BRD's actual master data — write no audit events at all. | Confirm the master-data entity list requiring before/after capture (FR-MDM-05). |
| E45 | **OCR performance target is contradicted in configuration.** The BRD sets a 60-second extraction target; the configured model client timeout is 180 seconds. | Approve a measured baseline and target, or the criterion is untestable (relates to D10). |
| E46 | **No cutover tooling exists** — no 2-year history migration tooling, no master-data validation harness, no parallel-run capability. | Confirm these are a separately scoped and funded Gate 10 workstream, not assumed present. |
| E54 | **Customer PO attachments 404 in production today.** `Controllers/FileController.cs` `DownloadAttachment` serves `Lead` through the evidence store, and its non-lead branch falls through to `_legacyStorage`, which the DI construction path leaves null — so the `CustomerPurchaseOrder` parent type added in Gate 3 cannot actually be downloaded. Found by the Gate 5 traceability agent, which declined to build on it and added a separate route for lot certificates instead. The route is also gated on `Leads/View`, which is the wrong permission for a commercial document. | Gate 3 remediation. Fix the storage fallthrough and give each parent type the permission that matches the document, rather than inheriting the lead's. Cheap, and it is a customer-facing 404 on evidence the buyer uploaded. |
| E52 | **The AI agent auto-approve spend cap is currency-blind, and this governs unattended commitment.** `AgentPolicy` stores the caps as bare `decimal` with no currency column, and `SourcingTools.cs:413-414` compares `LandedUnitCost × quantity` — denominated in the supplier quote's own currency — directly against the cap with no conversion. A cap of 10,000 therefore stops a 10,000 SAR award and a 10,000 USD award identically, so the same configured ceiling authorises ~3.75x more spend depending only on the supplier's quoting currency. Labelling the field SAR would have been exactly as false as the `$` it replaced, which is why the UI now states the real rule instead. | Being fixed: currency on the policy, conversion at every comparison site, and **fail closed** — refuse auto-approval and route to a human when no rate is available or the policy has no currency, never fall through to a raw numeric comparison. **If no exchange-rate source exists in the platform, that is a prerequisite and needs a decision**: a wrong rate silently authorising spend is worse than a refusal. |
| E53 | **A pasted price on the RFQ pricing screen silently posted the line with no price.** `ProcessRFQPage` stripped `'$ '` from input, but the formatted value has no space after the symbol, so the replace was a no-op, `Number()` returned `NaN`, and `NaN` serialises to `null` through `JSON.stringify`. Live and reachable, not latent. Fixed — an unparseable entry now leaves the previous price untouched instead of writing a corrupt figure. | Recorded as a pattern worth remembering rather than an open question: a display convention that is load-bearing in a write path fails silently, and the failure looks like a user error rather than a defect. |
| E50 | **Cumulative delivered quantity per order line does not exist**, so no document or screen can state how much of an order is outstanding. Confirmed by search: `DeliveredQuantity`, `CumulativeDelivered`, `QuantityDelivered` and `ShippedQuantity` return zero hits across the backend, and `ShipmentItem` carries only quantity. The delivery note therefore shows ordered and shipped-on-this-shipment, and deliberately omits a "remaining" column rather than computing one in the browser. | This is the FR-DLM-02 gap (partial deliveries tracked as cumulative delivered against awarded). The blocking question is a policy one, not an implementation one: **which shipment states count as delivered** — despatched, or only confirmed-received; and do cancelled and draft shipments net off. Answer that, then the field follows. |
| E51 | **The UI is hardcoded to US dollars in 85 places across 22 files, and hardcodes SAR in none.** For a KSA-first product the currency requirement is currently met in zero screens. The remediation is small — about four DTO additions and one shared formatter, not 85 edits — but two instances are not cosmetic: Copilot auto-approve thresholds are entered against a `$` adornment and applied to a SAR order book (~3.75x error in delegated automation authority), and `ProcessRFQPage` strips `'$ '` from input **on the write path**, so changing the display format without the parser silently corrupts entered prices. New tenants are also provisioned `USD`/`en-US` by default. | Being fixed. Recorded because the pattern — a display convention that is load-bearing in a write path — is worth remembering before the next formatting change. |
| E48 | **There is no supplier tax invoice record, and without one the input-VAT reclaim is unevidenced.** Excluding recoverable input VAT from landed cost (R15) implicitly books a reclaim. A reclaim requires a valid supplier tax invoice. Today the only tax figure in the system is a number a buyer typed into a supplier *quotation*, and it is discarded before the purchase order is even raised — `SupplierPurchaseOrder`, `SupplierPurchaseOrderLine` and `GoodsReceipt` carry no tax at all, and `GoodsReceiptLine` carries no value, so a three-way match is structurally impossible. The codebase states the gap in its own words at `CommercialDocuments/CommercialDocumentClassificationService.cs:121`: *"No authoritative Supplier Invoice aggregate exists yet."* The ZATCA reviewer's verdict: he would **disallow the entire input VAT claim for every open period**, on the face of the record, before any judgement is required. | Decide whether a supplier tax invoice record — even a thin header carrying supplier, invoice number and date, taxable amount, tax amount, currency and the scanned document's id — is in Phase 1. It is not in BRD v3.0, so it needs written approval. Note the cost of deferring: **every purchase made before it lands is a period whose input VAT cannot be evidenced.** |
| E49 | **No tax register exists, so the reconciliation a ZATCA audit opens with cannot be produced.** No output-VAT or input-VAT register, no period tax report, and no sales invoice ever posts to the general ledger — the GL exposes only bank adjustment, customer payment and customer refund, so there is no revenue journal and no VAT liability account for output tax to reconcile against. | Schedule the period tax register **before** the UBL/e-invoicing work in Gate 7, not after. It is the artefact the return is prepared from *and* the artefact the auditor reconciles to; building the e-invoicing pipeline first produces something compliant-looking that still cannot be audited. |
| E47 | **Historical quotation bulk-load is now structurally refused, and this is deliberate.** Closing the spine means `QuotationUploaderService` requires a customer RFQ that exists in Nexora and carries a commercial case. FR-PQH-01/02 requires two years of quotation history at cutover, and none of those historical quotations has a Nexora RFQ behind it, so every such row is refused today. Call path: `POST /api/QuotationUploader/upload`. **No exemption was added on purpose** — a migration back-door that skips the case is precisely how the spine rots, and it would reintroduce the case-less documents this gate spent its effort eliminating. | Gate 10 needs a governed history-import path that lands the RFQ and the quotation **together** as one transaction, inheriting one case. Confirm this is scoped and funded as part of the cutover workstream. Raised now rather than discovered at cutover. |

### Scope control

| ID | Finding | Decision needed |
|---|---|---|
| E36 | **~63k LOC of hand-written subsystems have no BRD v3.0 requirement** — a SaaS control plane, subscription billing, general ledger, bank reconciliation, treasury, AR/collections/dunning, AI copilot and several "intelligence" engines. Roughly **64% of hand-written backend subsystem code sits outside the Phase 1 ceiling**, and the finance engine alone is far larger than the shipment/delivery/traceability spine the BRD actually commits to. | Per BRD §10, out-of-scope features are **preserved but frozen or hidden**. Approve the freeze list, confirm no further investment, and decide whether the 22 Receivables/Collections/Banking/GL permission modules are hidden from tenant role matrices for the pilot. |
| E37 | **AI/copilot surfaces are excluded by the BRD** ("AI-predictive next-best-action recommendations" is explicitly out of scope) yet are shipped, routed and permission-gated — and have colonised the SLA engine, where a copilot-approval escalation is built while four of five BRD triggers are not. | Remove, feature-flag off for pilot, or formally amend the BRD to admit them? It is an audit finding either way. |

---

## Gate 4 engineering decisions (taken under standing delegation, 2026-08-09)

Taken by the CTO under the owner's standing instruction to own technical calls. Recorded here so
they are not re-litigated, and so a reviewer can see what was chosen **and what was rejected**.

| ID | Decision | Rationale |
|---|---|---|
| R10 | **Only an ACCEPTED supplier acknowledgement advances status to ACKNOWLEDGED.** A counter or a rejection stamps the acknowledgement fields but leaves `Status` alone. | A counter is the supplier asking for different terms; a rejection is a refusal. Neither is agreement. Showing either as "acknowledged" would tell a buyer their order is confirmed when it is not — and that is a promise the sales side then makes to the customer. |
| R11 | **A counter must name a revised lead time or a committed ship date; a rejection must name a reason.** | Otherwise the acknowledgement's only real effect is to silence the escalation sweep, which converts a control into a mute button. |
| R12 | **A non-positive SLA policy value means "not configured", not "zero tolerance".** | `TimeSpan.FromHours(0)` makes every dispatched order overdue the moment it is sent. The failure mode is a mass escalation on first sweep, after which recipients filter the channel and every real alert is lost with it. |
| R13 | **Allowed-status sets live next to the status constants, not inline in each method.** `OpenForReceipt` and `Cancellable` are shared. | Adding `ACKNOWLEDGED` silently broke goods receipt and cancellation, because each guard carried its own hand-written `or` chain. Centralising makes the next status a visible decision instead of a missed clause. |
| R14 | **The supplier acknowledgement escalation clock starts at dispatch, not internal approval.** | An order approved Monday and sent Thursday was charging the supplier three days it never had, which makes the metric indefensible in a supplier conversation. |

| R15 | **Recoverable supplier input VAT is not a cost and is excluded from landed cost.** Freight, duty and other charges stay in. Governed by one per-BU switch, `CommercialMatchingPolicy.SupplierInputTaxRecoverable`, defaulting to TRUE. | A VAT-registered KSA business reclaims input VAT. Carrying it in landed cost did not merely add it — customer price is `landed / (1 − margin)`, so the VAT was **marked up by the margin** and then output VAT was charged on top of that. On a worked example the quoted net price was exactly 1.15× correct: the firm was quoting ~15% high into competitive tenders while reporting 20% margin on a true 30.4%. Under-pricing loses money; over-pricing loses the tender and nobody ever learns why. |
| R16 | **`CK_supplier_purchase_order_lines_Costs` drops `LandedUnitCost >= UnitCost`, keeping only positivity.** | Landed cost subtracts the supplier discount, so a discounted line legitimately lands below list price. The constraint asserted an invariant commerce does not obey. Clamping in the service instead was rejected: it would silently distort the cost figure every downstream price is built from, which is worse than the insert failing loudly. |

| R17 | **Output VAT must be derived server-side, and R15 may not reach a tenant without it.** One `OutputTaxRatePercent` on the per-BU policy (default 15), recomputed whenever a price moves, with the send gate refusing a line whose tax was never derived. | The finance panel found that nothing computes output VAT: `QuoteItem.TaxAmount` is operator-typed, defaults to null, and validation rejects only negatives. The input-VAT error R15 removed was **silently cancelling** the missing output-VAT leg, because both are 15% on nearly the same base. Worked example: before R15 a price of 172.50 deemed VAT-inclusive nets 150 against a true cost of 120 — a real 20% margin. After R15 alone, 150 nets 130.43 against 120 — **8%**. Under KSA law a price with no separately stated VAT is deemed VAT-inclusive, so the firm would owe 15/115 ≈ 13.04% out of every domestic sale. R15 is right; shipping it alone is worse than not shipping it. |
| R18 | **Input-tax recoverability becomes a percentage, not a boolean.** `SupplierInputTaxRecoverablePercent`, default 100, constrained 0–100. | Recommended independently by the CPA and the Tax Consultant. A VAT-registered trader making partly exempt supplies recovers pro-rata, and the irrecoverable share is a cost of the asset under IAS 2.11. A boolean forces a choice between overstating and understating cost. It is still one customer-set number per business unit — the owner's steer is preserved exactly — and converting a boolean to a percentage after tenants hold data is the expensive version of this change. |
| R19 | **A supply that is not standard-rated must be representable.** One `TaxCategory` on the quote line — STANDARD / ZERO_RATED_EXPORT / EXEMPT / OUT_OF_SCOPE_RCM — with a reason required when it is not STANDARD. | Today a correctly zero-rated export and a Riyadh sale where the rep forgot the 15% are byte-identical records: nothing states the treatment and nothing holds the evidence. The user picks the category; the system does not infer it, which is R8's own philosophy applied to the sell side. Doing this before ZATCA e-invoicing avoids retro-fitting the field onto live quotes, and before then the serializer would have to hardcode "standard rated" and mis-declare every export. |
| R20 | **Historical landed costs are recomputed in full before pilot — no calculation-version stamp, and explicitly NOT "open cases only".** | The affected set is not one column: it runs through supplier quoted items, awards, PO lines, PO totals, sourcing decisions, the customer prices derived from them, quote totals and inventory unit cost. A stamp on the cost column cannot segregate the **prices** built from it, which is the number that matters. "Open cases only" is the worst option — it guarantees a mixed basis *inside a single commercial case* with nothing recording which is which. This is a pre-launch system with no real customer data, so the recompute is nearly free today and the window closes on the day the first tenant goes live. |

| R21 | **Arriving at the warehouse does not raise a goods receipt. Receipt stays a deliberate human act — and posting one *settles* the inbound shipment that carried the material.** The crossing runs receipt → shipment, not shipment → receipt. | The decisive argument is that `MaterialLotRecorder` **refuses** a batch-tracked line with no supplier batch number, and a serial-tracked line without one serial per unit. A milestone that tried to raise a receipt would therefore either invent those identifiers — poisoning certificate evidence and recall for the life of the tenant — or crash the milestone write. Two supporting reasons: the model already separates the events, since putaway lead time is the working days between goods *reaching* the warehouse and being available, and that number is zero by definition if arrival were the receipt; and a receipt moves on-hand quantity, so auto-raising one turns a mis-keyed milestone into a financial misstatement. Allocation is oldest-shipment-first, recorded in the event payload so a wrong attribution is correctable rather than silent. |

| R22 | **Module 7 is cut to evidence, not operations. Vehicle and route scheduling, and live GPS/vehicle tracking, are OUT — they belong to a TMS. SMS and WhatsApp notification are deferred; email stays.** Ratified by the product owner 2026-08-09, narrowing FR-DLM-05 and FR-DLM-06. | The dividing line is *record what happened at the door* versus *plan the vehicle that went there*. Tech Connect is a trader using third-party carriers and owns no fleet, so route optimisation solves a problem it does not have, while POD, delivery status and exception capture are what let it get paid and defend a dispute. **What stays from FR-DLM-05 is the status ladder** — Scheduled, Dispatched, In Transit, Delivered, Delivery Exception — which is a lifecycle, not a scheduler. **GPS stays only as a coordinate stamped at the moment of signature**, which is evidence on the POD record; it does not become a tracking feed. SMS and WhatsApp need provider contracts, per-message cost and KSA sender-ID registration, none of which is scoped. All of it is reversible: adding a scheduler later wastes nothing built now. |

| R23 | **FR-DLM-07 records the commercial fact and its consequence, not a claims or corrective-action workflow.** Ratified by the product owner 2026-08-09: *"if something damages during delivery then logistics should take care of it, not sales, as this is core sales engine."* | Nexora is the sales spine, so what it owes on a rejection, short receipt or damaged delivery is entirely commercial: the **accepted** quantity is less than the despatched quantity, the unaccepted part must not be invoiceable, the order line needs a visible re-supply-or-credit decision, and the commercial case must record why. Billing a customer for goods they refused is how a receivable becomes a dispute — that is the consequence worth engineering. **Out:** carrier claims, liability assignment, insurance recovery, CAPA or root-cause workflow, damage-claim evidence packs, and any RMA or reverse-logistics movement. Those are the carrier's process, run outside this system. **Consequence for the model:** despatched and accepted are two different numbers, and invoicing and fulfilment key on **accepted**. |

**Rejected in this gate, deliberately:** auto-advancing a countered order to ACKNOWLEDGED once the
buyer accepts the counter (needs a governed accept-the-counter action, not an implicit transition);
and allowing cancellation of an IN_PRODUCTION or SHIPPED order (that is a commercial negotiation,
not a status change).

---

## How to use this register

1. Nothing on the E-series list is a bug report. Each is a **question with a cost attached**.
2. Items **E29, E28, E5, E19, E9 and E24** should be answered first — they either have external
   lead time, block a whole gate's design, or are a live demo risk.
3. When an item is decided, record the decision, the date, the decider, the affected FR IDs and
   the acceptance-criteria change. Then, and only then, engineering may implement it.
