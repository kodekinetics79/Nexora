# Nexora — Client Pilot Handoff

**Document status:** issued for client acceptance
**Release covered:** `b496e2e` (backend on Render, frontend on Vercel)
**Date of issue:** 2026-09-03
**Issued by:** Kodekinetics
**Supersedes:** nothing. This is the first client-facing handoff document.

---

## How to read this document

Every capability claim below names the screen or the API endpoint behind it, so any statement here
can be checked against the running system rather than taken on trust.

Two phrases are used deliberately and mean different things:

- **"Demonstrated on the deployed system"** — a person has driven this on
  `https://nexora1-ai.vercel.app` against the production backend and seen the result.
- **"Not yet demonstrated on the deployed system"** — the code exists and is covered by automated
  tests, but nobody has yet driven it end to end on production. It is a pilot activity, not a
  delivered fact.

Where something is out of scope, §2 gives the exact sentence to use when a stakeholder asks about it
in the room. Those sentences are the agreed wording; please do not soften them.

---

## 1. What the pilot covers

The pilot covers the front half of the commercial journey — an enquiry arriving, becoming a
governed record, being sourced, and being priced into a customer quotation — plus the setup and
access controls a real team needs around it.

### 1a. Demonstrated on the deployed system

| Stage, in the operator's words | Where it happens | What has been shown |
|---|---|---|
| **A customer email arrives and turns into an enquiry the team can see.** | Inbox → **Inbound mail** (`/procurement/leads/inbound-mail`), backed by the mailbox configured at Setup → **Email Inboxes** (`/setup/mailboxes`) | Live mail polled from a real mailbox on the production tenant, reconciled into a Lead. Every poll decision is visible on the Inbound mail screen, including the messages it declined and why. |
| **A document dropped in by hand turns into the same kind of enquiry.** | Inbox → **Upload documents** (`/procurement/leads/manual-upload`) | Uploaded documents are virus-inspected before they are parsed and stored as immutable evidence against the enquiry. |
| **The system reads the document and a person checks what it read.** | Inbox → **Documents to check** (`/procurement/extraction/review`) | Line-level extraction with a human review step. Nothing is promoted from an extraction without a person confirming it. |
| **Enquiries land with the right person.** | **Leads** (`/procurement/leads/all`), Setup → **RFQ Routing Rules** (`/setup/routing-rules`) | Routing by customer, category or business unit, plus a named fallback owner so nothing lands nowhere (`GET/PUT /api/commercial-routing/default-owner`, added in PR #143). |
| **A manager decides whether to bid, and on which lines.** | Lead detail → participation decision | Full bid, partial bid and no-bid are explicit, recorded decisions. Only approved lines may be promoted. |
| **An approved enquiry becomes a formal RFQ.** | **RFQs** (`/procurement/rfqs/all`) | RFQ Promotion is the only route from a Lead to an RFQ, so an RFQ always carries its originating evidence. |
| **A draft customer quotation is built and priced.** | **Quotes** (`/sales/quotes`), quote detail | A quotation can be drafted, priced, revised and previewed as a PDF (`GET /api/Quote/{id}/pdf`). |
| **Who can see and do what.** | Setup → **Users** (`/security/users`), **Roles & Permissions** (`/security/roles`) | Role boundaries are enforced by the server, and were re-verified on the deployed release for a Sales Manager, a Sales Representative and a deliberately restricted user. |
| **A revoked or deactivated user stops working immediately.** | Setup → **Users** | Added in PR #142: deactivating a user, changing their role, or resetting their password invalidates their existing session within 30 seconds rather than at token expiry. |

### 1b. Built and tested, but a pilot activity rather than a delivered fact

These are in the pilot's scope to prove. **None of them has yet been demonstrated on the deployed
system.** As of this release, production holds **zero sent quotations, zero client purchase orders
and zero supplier purchase orders**.

| Stage | Where it happens | Status |
|---|---|---|
| Sending a supplier RFQ to suppliers | **Sourcing** → RFQs needing sourcing (`/procurement/rfqs/all?state=requires-sourcing`) | Not yet demonstrated on the deployed system. |
| Capturing supplier quotations and comparing them | **Supplier quote inbox** (`/procurement/supplier-quotes`) | Not yet demonstrated on the deployed system. |
| Sending a customer quotation by email | Quote detail → **Send to customer** (`POST /api/Quote/{id}/email`), gated by the price-source attestation (`POST /api/Quote/{id}/price-attestation`) | Not yet demonstrated on the deployed system. |
| Receiving a client PO and matching it to the quotation | **Client PO inbox** (`/sales/client-pos`) | Not yet demonstrated on the deployed system. |
| Raising a supplier purchase order | **Supplier purchase orders** (`/suppliers/purchase-orders`) | Not yet demonstrated on the deployed system. |
| Goods receipt, stock, shipment and proof of delivery | **Inventory**, **Fulfilment** (`/sales/shipments`) | Not yet demonstrated on the deployed system. |
| Invoice and payment follow-up | **Receivables** (`/sales/finance`) | Not yet demonstrated on the deployed system, and see the ZATCA exclusion in §2. |

Getting the first real transaction all the way through §1b is the substance of the pilot. Everything
in §1a is what makes that attempt worth making.

---

## 2. What is explicitly excluded

Each row gives the sentence to use in the room, and why the exclusion exists. These are exclusions
from **this pilot**, not permanent product decisions, unless the row says otherwise.

### 2.1 ZATCA e-invoicing

> **"Nexora does not generate ZATCA e-invoices today. There is no e-invoicing code in this release.
> It is scheduled last, deliberately, and will be built and proven against the ZATCA sandbox before
> anyone asks Tech Connect for production credentials."**

**Why:** decision **R1** in `docs/NEXORA_PHASE1_DECISION_REGISTER.md`. ZATCA was sequenced last so
that the pipeline — UBL 2.1 serialisation, invoice hash chaining, XAdES signing, TLV QR — is built
and certified against the sandbox, which needs no client credentials. R1 records the accepted
consequence in its own words: *"pilot acceptance requires at least one compliant e-invoice. Until
this gate completes, that criterion is unmet."*

**What this means for acceptance:** the BRD's pilot criterion *"at least one successfully generated
ZATCA-compliant e-invoice through Nexora"* is **carved out of this pilot's acceptance test**. See
§8.3 for the carve-out and its date.

### 2.2 Arabic, right-to-left layout and Hijri dates

> **"The interface is English only in this release. There is no Arabic UI, no right-to-left layout,
> and no Hijri calendar handling. We will not describe Nexora as bilingual."**

**Why:** decision **R6**, an approved deviation from the bilingual requirements in FR-RFQ-04,
FR-QTM-01, FR-QTM-06, FR-DLM-01 and the localisation NFR. The quotation and delivery-note PDFs name
this as a gap rather than half-rendering Arabic. Arabic OCR is also absent — only the English
language pack ships.

**Open item worth raising with the client:** Hijri *date handling* is a data-correctness problem
rather than a language problem. Saudi government tenders publish closing dates in Hijri, and today
the system has no Hijri parsing and discards the closing time. An English-only interface can still
parse and store a correctly converted Hijri deadline. **Getting this wrong loses bids.** R6 flags
this carve-out for the client's decision; it is not yet approved or built.

### 2.3 Data residency — the platform is not hosted in Saudi Arabia

> **"Nexora currently runs in the United States: the application in Render's Oregon region, the
> database in Neon's US-East-1, the web front end on Vercel. None of those three vendors offers a
> Saudi region. PDPL residency is an open decision that needs either an approved transfer mechanism
> or funded re-platforming, and it is yours to make."**

**Why:** `render.yaml:45` sets `region: oregon`; the Neon project `Nexora-neon-cyclamen-house` sits
in AWS `us-east-1`; `vercel.json` sets no region. Recorded as **E38**, and decision **R2** states the
intended answer — client-hosted deployment inside the client's own KSA infrastructure, with
S3-compatible storage (MinIO) so the existing content-addressed, hash-verified evidence layer is kept
rather than rewritten. **R2 is not yet executed, and its final confirmation rests with the client.**
This is an infrastructure programme with weeks of lead time, not a configuration change.

### 2.4 Availability — every deploy is a real outage, and nothing watches the service

> **"Nexora currently runs as a single instance with a disk attached, which means a deployment
> cannot be rolled without dropping the service. Every release is a short, planned outage. We do not
> yet have uptime monitoring, so today we find out about an incident when someone tells us."**

**Why:** the backend runs one instance with a persistent disk mounted at `/var/data`
(`render.yaml:142-145`). A service with a disk cannot be replaced zero-downtime — the file states
this outright. The application code is already written to run multiple instances (work claims,
advisory locks, send-once ledgers), so this is hosting spend rather than rework.

**The incident to disclose, not hide:** on **2026-09-03** a deployment caused a **90-minute
production outage**. The cause was host-level inotify (file-watch handle) exhaustion, and it was
fixed by setting `DOTNET_USE_POLLING_FILE_WATCHER=1`. It is fixed, and it is exactly the class of
event that a single-instance deployment turns into downtime rather than a blip.

**Consequence for the BRD:** the non-functional requirement of **99.5% availability during Saudi
business hours is not committed to in this pilot.** It is structurally unreachable on a single
instance. Either multi-instance hosting is funded, or the target is renegotiated — decision R27.

### 2.5 Backup and restore — snapshots exist, a restore has never been rehearsed

> **"Backups exist. A restore has never been rehearsed, so we will not claim a recovery time.
> Rehearsing it is a named pilot deliverable with an owner and a date."**

**Why:** Render's disk page shows daily snapshots of the 5 GB evidence volume (seven consecutive
snapshots were observed for 2026-08-23 to 2026-08-29). The Neon project reports
`history_retention_seconds=21600` — a **six-hour** point-in-time recovery window, which is
considerably shorter than the BRD's RPO of 24 hours implies is safe. **No restore button has ever
been pressed.** An untested backup is not a backup. The restore runbook and the first rehearsal are
being produced separately and are referenced in §7.

### 2.6 Multi-factor authentication for tenant users

> **"Multi-factor authentication protects the Kodekinetics operator console today. It does not yet
> exist for your users signing in to Nexora itself."**

**Why:** MFA enrolment, policy and browser trust are implemented under the platform control plane
(`Backend/ERP_RFQ_Automation/Platform/Auth/PlatformMfaPolicy.cs` and siblings), where every
privileged policy requires a second factor. The tenant sign-in path (`Controllers/AuthController.cs`)
has no MFA. This is a gap against the BRD's security NFR ("MFA through Azure AD or an equivalent
identity provider for every login") and must be stated as one.

### 2.7 The AI assistance features are advisory, and their accuracy is unmeasured

> **"The opportunity priority, pricing suggestions and coaching features are advisory. They run in
> shadow mode, they never change a price or a decision on their own, and their accuracy is recorded
> as unmeasured. We will not sell them as prediction."**

**Why:** these features carry no accuracy figure — the value recorded is `null`, and the code says so
in its own words. `CommercialIntelligence/Opportunity/OpportunityPriorityApplicationService.cs:122`
returns *"Not measured: shadow action recommendations are not calibrated win predictions."* The
pricing engine refuses to mutate RFQ prices at all
(`Intelligence/Pricing/PricingEngine.cs:227`) and labels its output *"(shadow advice)"*. The BRD
itself puts predictive pricing and AI next-best-action **out of scope for Phase 1**.

### 2.8 Also out of scope for Phase 1 (from the BRD, unchanged)

Customer self-service portal; supplier EDI/cXML; automated e-auction or reverse bidding; live carrier
API integration; live production ERP integration and real-time master-data sync; blockchain
provenance; own-fleet route optimisation and telematics; a full WMS with bin slotting; embedded
self-service BI authoring; touchless customer-PO matching (human review is retained by design).

Additionally deferred under decision **R26**: SMS and WhatsApp notifications (email works), carrier
tracking APIs (the manual path is complete), governed stock-count sessions (the variance report
exists), and invoice payment-due reminders (blocked on the client's Finance decision, D7).

### 2.9 Two-year history load

The BRD's cutover criterion to migrate at least two years of purchase and quotation history is **not
part of this pilot**. The commercial-case spine deliberately refuses a historical quotation that has
no Nexora RFQ behind it, and the governed import that would land both together in one transaction is
recorded as **E47** and is not yet funded. Raise this as a scheduling decision, not a defect.

---

## 3. Named participants and roles

The deployed lane expects three tenant personas plus a tenant owner. Please complete the blanks
before the pilot start date; role provisioning is a setup step (§4.4) and each person must be a
distinct account. **Do not share a login between two people** — the role boundaries the pilot is
testing become meaningless if two humans sit behind one account.

| # | Role in the pilot | What they do in Nexora | Name | Email | Mobile | Signed up on |
|---|---|---|---|---|---|---|
| 1 | **Tenant Owner** (client) | Owns the pilot commercially. Approves setup values, signs pilot acceptance, and is the single point of decision on the open items in §9. | ____________ | ____________ | ____________ | ____ / ____ |
| 2 | **Tenant Administrator** (client) | Completes the setup checklist in §4.4, invites and deactivates users, configures the mailbox, tax rate and quote format. May be the same person as #1. | ____________ | ____________ | ____________ | ____ / ____ |
| 3 | **Sales Manager** (client) | Governs the team. Sets the fallback routing owner, makes bid/no-bid participation decisions, reviews exceptions and the team's queue. Holds manager-rank authority over Leads and Quotations; does **not** need the Users screen. | ____________ | ____________ | ____________ | ____ / ____ |
| 4 | **Sales Representative** (client) | Works the queue. Triages the Inbox, checks extracted documents, promotes to RFQ, drafts and prices quotations. Member rank, no manager authority, no Users screen. | ____________ | ____________ | ____________ | ____ / ____ |
| 5 | **Sales Representative** (client, second) | As above. A second rep is recommended so ownership, routing and hand-over can actually be observed. | ____________ | ____________ | ____________ | ____ / ____ |
| 6 | **Finance Officer** (client) | Reviews Receivables (`/sales/finance`) and the commercial figures on a quotation. Note the boundary in §2.1 and §2.8: there is no ZATCA invoicing and no payment-due reminder engine in this release, so this role's pilot scope is review and comment. | ____________ | ____________ | ____________ | ____ / ____ |
| 7 | **Kodekinetics support contact** (primary) | Named recipient of every P1 and the escalation path in §5. | ____________ | ____________ | ____________ | — |
| 8 | **Kodekinetics support contact** (backup) | Covers the primary's absence. | ____________ | ____________ | ____________ | — |
| 9 | **Kodekinetics delivery lead** | Owns the release calendar (§6), the restore rehearsal (§7) and pilot acceptance (§8). | ____________ | ____________ | ____________ | — |

**Personas the pilot does not staff:** procurement, logistics/warehouse and platform administration.
Sourcing and inventory screens exist and are reachable, but no dedicated pilot persona is assigned to
them; they are exercised by the Sales Manager where the journey requires it.

---

## 4. Environment and access

### 4.1 URLs

| What | URL |
|---|---|
| Nexora (the application your team uses) | `https://nexora1-ai.vercel.app` |
| Sign in | `https://nexora1-ai.vercel.app/login` |
| Password reset | `https://nexora1-ai.vercel.app/forgot-password` |
| Account activation (from an invitation email) | `https://nexora1-ai.vercel.app/activate/<token>` |
| Kodekinetics operator console (**not for client users**) | `https://nexora1-ai.vercel.app/platform` |
| API (for reference; your team does not call it directly) | `https://nexora-fyjw.onrender.com` |
| Service health, readable by anyone | `https://nexora-fyjw.onrender.com/ready` |

Supported browser: a current version of Chrome, Edge, Safari or Firefox. The application is
responsive and usable on a tablet; it is designed for a desktop screen.

### 4.2 How a user is invited and activates

1. The Tenant Administrator opens Setup → **Users** (`/security/users`) and adds the person.
   The screen defaults to **Send invitation**, with *"Set a password instead"* offered as a
   secondary control (PR #141).
2. Nexora creates the account dormant — it has no usable password — and emails a single-use
   activation link. **The link is valid for 72 hours.**
3. The person clicks the link, lands on `/activate/<token>`, sets their own password, and is signed
   in. The token is consumed at that moment and cannot be reused.
4. If the invitation expires or is lost, the administrator uses **Resend invitation** on the same
   screen (`POST /api/User/{id}/resend-invitation`), which issues a fresh token.
5. Where the tenant has its own verified mailbox configured, the invitation email arrives **from the
   company's own address** rather than from Nexora (PR #141, decision B4). Where it does not, it
   arrives from the platform address.

**Deactivating a person takes effect within 30 seconds**, not at the end of their session — the
release adds a per-request session check (PR #142). The same applies to a role change or a password
reset: everything they hold is invalidated, including their own other sessions.

### 4.3 What a user does on day one

A representative signing in for the first time should be able to do this without training. If they
cannot, that is a defect and we want to hear about it.

1. Sign in at `/login`. The landing place is the **Inbox** (`/inbox`).
2. **Inbox → Needs you** lists the work waiting on them. On an empty tenant it says so plainly, and
   offers the two ways to put work into it: **Upload a document** and **Connect the mailbox**
   (PR #143). It does not show an empty grid with no explanation.
3. **Inbox → Documents to check** is where the system's reading of a document is confirmed or
   corrected, line by line.
4. **Leads → All inquiries** is the full picture of live enquiries; **Untriaged only** narrows it to
   what has not been dealt with (PR #143).
5. Opening an enquiry shows the source document beside the extracted lines, so the evidence and the
   record are never separated.
6. From an approved enquiry, **Promote to RFQ**; from an RFQ, **Price customer quote**. Where an
   action is not yet available, the button is present, disabled, and **states the reason and the
   screen that unblocks it** (PR #143). A greyed-out control with no explanation is a defect.
7. Unsaved quotation work is held and offered back with **Restore / Discard** if the user navigates
   away, and leaving a dirty screen asks first (PR #143).

### 4.4 Minimum setup before the pilot starts

Eight screens. The Tenant Administrator can complete all of them in a working session, and the
journey does not function correctly until they are done. Tick each item and record who did it.

| ✔ | # | Item | Screen | What "done" means | By | Date |
|---|---|---|---|---|---|---|
| ☐ | 1 | **Company identity** | Setup → **Business Units** (`/setup/business-unit`) | Legal name, CR number and VAT registration number recorded for the entity that issues quotations. These print on the customer document. | ____ | ____ |
| ☐ | 2 | **Currency** | Setup → **Currencies** (`/setup/currency`) | The functional currency you trade in is present and set. The provisioning wizard now defaults to **SAR** (PR #141). Note: Kodekinetics bills in USD as a platform constant; that is separate from your trading currency and does not change it. | ____ | ____ |
| ☐ | 3 | **Mailbox** | Setup → **Email Inboxes** (`/setup/mailboxes`) | The IMAP inbox enquiries arrive in is connected and polling, and the SMTP account quotations are sent from is connected and tested. Use **Test connection**, then **Send test** (`POST /api/Mailbox/{id}/send-test`) which sends one real message to yourself. Confirm the banner on `GET /api/Mailbox/outbound-status` names **your** address, not the Nexora platform address — see §2 and §9.6. | ____ | ____ |
| ☐ | 4 | **Tax rate** | Setup → **Commercial Policy** (`/setup/commercial-policy`) | The VAT rate is entered (15% in KSA), with rounding and tolerance set. **This is load-bearing:** until a tax rate exists, a quotation cannot be sent, and the Send button now disables itself and says so rather than failing later (PR #143). Nexora does not infer your rate — you state it. | ____ | ____ |
| ☐ | 5 | **Letterhead and quote format** | Setup → **Quote Format** (`/setup/quote-format`) | Logo, header, quotation numbering and the standard terms that print on the document. Preview one PDF (`GET /api/Quote/{id}/pdf`) and have the Tenant Owner approve how it looks before a customer sees it. | ____ | ____ |
| ☐ | 6 | **Users and roles** | Setup → **Users** (`/security/users`), **Roles & Permissions** (`/security/roles`) | Every person in §3 has their own account, holding the role that matches their row. Verify by signing in as the Sales Representative and confirming the Users screen is not reachable. | ____ | ____ |
| ☐ | 7 | **Warehouse** | Setup → **Warehouses** (`/setup/warehouse`) | At least one warehouse with a code and address, so stock and delivery have somewhere to belong. | ____ | ____ |
| ☐ | 8 | **Suppliers** | **Suppliers** (`/suppliers`), bulk import at `POST /api/SupplierUploader/upload-template` | The suppliers you actually solicit, with a working contact email each. An RFQ cannot be sourced to a supplier who is not here. | ____ | ____ |

**Recommended ninth step, not strictly required to sign in:** Setup → **RFQ Routing Rules**
(`/setup/routing-rules`). Set the **fallback routing owner** so an enquiry that matches no rule still
lands with a named person rather than nowhere. This control was added in PR #143 specifically because
there was previously no way to set it.

---

## 5. Support and escalation

### 5.1 Contact

| | |
|---|---|
| **Primary support contact** | ____________________ (§3, row 7) |
| **Email** | ____________________ |
| **Mobile / WhatsApp for P1 only** | ____________________ |
| **Backup contact** | ____________________ (§3, row 8) |
| **Escalation to delivery lead** | ____________________ (§3, row 9) |

### 5.2 Hours — to confirm at signature

**Proposed:** Sunday to Thursday, 09:00–18:00 Arabia Standard Time, excluding Saudi public holidays.
Outside those hours a P1 may be raised by mobile and will be acknowledged on a best-effort basis.
There is no contracted 24/7 cover in the pilot.

### 5.3 Severity, and what each one means

| Severity | Definition | Target acknowledgement | Target workaround or fix |
|---|---|---|---|
| **P1 — critical** | Nobody can sign in; enquiries are not being captured from the mailbox at all; a customer-facing document is wrong in a way that has commercial consequence; or data is at risk of loss. | 1 working hour | Same working day, or a stated plan by end of day |
| **P2 — major** | A journey stage is blocked for one role or one record type, and there is no workaround. | 4 working hours | 3 working days |
| **P3 — minor** | Wrong, confusing or missing behaviour with a workaround available. | 1 working day | Next release |
| **P4 — cosmetic or request** | Wording, layout, or a change request. | 2 working days | Scheduled, not committed |

The BRD does not define severity levels or their SLAs (**decision D10**), so the table above is
Kodekinetics' proposal. **It becomes binding only when the Tenant Owner signs it**, and §8.2 depends
on it: the acceptance criterion *"no critical defect left open beyond its agreed SLA"* has no meaning
until "critical" and "its SLA" are agreed here.

### 5.4 How we currently find out that something is wrong — stated honestly

**There is no uptime monitoring and no alerting on this deployment today.** In practice that means:

- If Nexora goes down at 02:00, nobody is paged. We learn about it when a user tells us, or when an
  engineer next looks.
- The service does publish its own health at `https://nexora-fyjw.onrender.com/ready`, which reports
  **eleven** checks: database, evidence storage, storage capacity, malware scanner, extraction
  worker, quote-delivery worker, procurement-dispatch worker, background workers, email polling,
  OCR engine and outbound email. All eleven were healthy on release `b496e2e`. **Anyone can open
  that URL.** It is a page you can check yourself; it is not a system that tells us when it changes.
- Please report anything that looks wrong immediately rather than assuming we already know.
  **In this pilot, you are our monitoring.**

**When this changes:** external uptime monitoring against `/ready`, with alerting to the primary
support contact, is committed for delivery by **____ / ____ / 2026** (date to be set with the Tenant
Owner at signature). Until that date is met, the paragraph above is the true detection story and will
not be described any other way.

---

## 6. Change and deploy policy

### 6.1 Deployments cause a short outage — this is not incidental

The backend runs as a **single instance with a persistent disk attached** (`render.yaml:142-145`).
A service with a disk cannot be replaced without downtime: the old container must release the volume
before the new one can take it. A normal deploy therefore drops the service for roughly **one to
three minutes**. Migrations run at container boot, which is deliberate and safe, but adds to that
window.

On **2026-09-03**, one deployment turned into a **90-minute outage** when the host exhausted its
file-watch handles. That is fixed (`DOTNET_USE_POLLING_FILE_WATCHER=1`). It is disclosed here because
it shows what the single-instance topology does to an ordinary bad day, and because §2.4 and §8.2
depend on the client understanding it.

The path out of this is known and costed: the application already supports multiple instances, and
the evidence store has been moved to object storage behind a switch (PR #142), which is what allows
the disk to be detached. Detaching the disk and running more than one instance is a hosting decision
in §9.

### 6.2 The agreed release window — to confirm at signature

**Proposed:** planned deployments occur **outside Saudi business hours**, in a window of
**22:00–02:00 AST on Thursday or Friday**. Sunday to Thursday daytime is protected.

### 6.3 What the client is told, and when

| Change | Notice to the client | Who is told |
|---|---|---|
| Planned release | **At least 24 hours beforehand**, naming the window and what changes | Tenant Owner and Tenant Administrator |
| Configuration change with visible effect (for example enabling a stricter session policy) | At least 24 hours beforehand | Tenant Owner and Tenant Administrator |
| Emergency fix for a P1 | Before it is applied where the delay is tolerable; immediately afterwards where it is not | Tenant Owner, and everyone at the next daily check-in |
| Unplanned outage | As soon as it is known, and again when service is restored | Tenant Owner and Tenant Administrator |

**No release goes out during the pilot without the Tenant Owner being told first.** After each
release, Kodekinetics confirms the deployed build identity (`GET /build-identity` must report the
exact merge commit) and re-checks all eleven readiness checks before declaring the window closed.

---

## 7. Rollback and recovery

### 7.1 If a release goes wrong

1. **Detect.** Either a user reports it or the post-deploy check fails: the deployed commit at
   `GET /build-identity` does not match the intended release, or `/ready` reports an unhealthy check.
2. **Decide within 15 minutes** whether to fix forward or roll back. Fixing forward is preferred for
   a cosmetic or single-screen fault; rollback is preferred for anything touching sign-in, intake or
   a customer-facing document.
3. **Roll back the application** by redeploying the previous known-good image on Render. The current
   known-good release is `b496e2e`. This is a further short outage, per §6.1.
4. **The database is the constraint on rollback, not the application.** Migrations apply at boot and
   the previous image cannot be rolled back onto a schema that no longer exists. Any release
   containing a schema change is therefore accompanied by a stated rollback position before it ships,
   and is scheduled inside the §6.2 window with the Tenant Owner informed.
5. **Communicate** per §6.3, including a written note of what happened, within one working day.

### 7.2 If data must be recovered

**Read this honestly: a restore has never been rehearsed on this deployment.** What exists today:

| Layer | What is in place | What is *not* proven |
|---|---|---|
| Database (Neon, `us-east-1`) | Point-in-time recovery. The project's retention was measured at **six hours** (`history_retention_seconds=21600`). | No restore has been performed. Six hours is shorter than the BRD's 24-hour RPO implies; extending it is a hosting decision. |
| Evidence and documents | Governed evidence is in object storage; daily snapshots exist on the 5 GB volume (seven consecutive daily snapshots observed for 2026-08-23 to 2026-08-29). | No snapshot has ever been restored. Consistency between a database restore and an evidence-store restore has never been tested. |
| Recovery targets | The BRD states RPO ≤ 24 hours and RTO ≤ 8 hours. | **Neither figure is committed to in this pilot**, because neither has been measured. |

**The named deliverable:** a written restore runbook and a rehearsed restore drill — into an isolated
environment, with row counts and content hashes compared, outbound workers disabled, and the measured
RPO and RTO recorded. The runbook is being written under this same release cycle and will live at
`docs/RUNBOOK-RESTORE.md`. **Until the rehearsal is done and its numbers are in this section, no
recovery time is promised to anyone.**

| Restore rehearsal owner | Target date | Completed |
|---|---|---|
| ____________________ | ____ / ____ / 2026 | ☐ |

### 7.3 The client's rights over its own data — already implemented

These are not promises; they are built and enforced today.

| Right | How it is exercised | Controls on it |
|---|---|---|
| **Take a full export** of every lead, quotation, order, customer and supplier record | `POST /api/platform/tenants/{id}/offboarding/export`, run by Kodekinetics on the Tenant Owner's written instruction | Returns a signed JSON document. The response carries a **SHA-256 of the content**, a **receipt id** and the **total row count**, so the client can prove the export is complete and unaltered. Restricted to the platform Owner role and audited as a high-risk operation. |
| **See what a deletion would remove, before agreeing to it** | `GET /api/platform/tenants/{id}/offboarding/purge-preview` | Row-count preview, Owner-only. |
| **Schedule deletion, and change your mind** | `POST .../schedule-deletion`, reversed by `POST .../cancel-deletion` | Starts a retention clock. Deliberately reversible, and takes a reason. |
| **Erase personal data while keeping the commercial record** | `POST .../erase-personal-data` | A distinct operation from deletion, available at any point before it — the shape PDPL and GDPR-style erasure requests actually need. |
| **Destroy the tenant permanently** | `POST .../purge` | Refused until the retention window has elapsed. Requires a reason and the tenant's exact name typed back. Audit records survive the purge by design. |

Two principles behind this, stated so they are not a surprise:

- **The tenant owns its data lifecycle.** Kodekinetics does not delete client records on the client's
  behalf. The Tenant Owner decides; we execute and evidence it.
- **Audit records always survive.** A purge destroys the commercial records; it does not erase the
  log of who did what.

Nexora's tenant-side data reset (`/api/TenantDataReset`) — which wipes transactional data so the same
emails can be re-ingested from a clean slate — is **refused in Production whatever the configuration
says**. It is a development convenience and is not available on your environment.

---

## 8. Acceptance criteria for the pilot

Pilot period: **____ / ____ / 2026 to ____ / ____ / 2026** (BRD §8 anticipates a three-month
incubation after Go-Live).

### 8.1 Criteria carried directly from the BRD, and deliverable now

| # | Criterion | How it is measured | Target |
|---|---|---|---|
| A1 | **Every one of the pilot account's RFQs is captured in Nexora.** | Count enquiries visible in **Leads** (`/procurement/leads/all`) against the client's own record of RFQs received for that account over the same period. Messages that arrived but produced nothing are visible on **Inbound mail** (`/procurement/leads/inbound-mail`) and count as failures, not as absences. | **100%** |
| A2 | **Average RFQ response time improves against the agreed baseline.** | Measured from enquiry capture to quotation sent. | Target improvement of **____%** against a baseline of **____ hours**, **both agreed in writing in pilot week 1**. Decision D11 records that the BRD supplies neither figure; without them this criterion is unmeasurable and cannot be assessed. |
| A3 | **No critical defect left open beyond its agreed SLA.** | Tracked against the severity table in §5.3, **once the Tenant Owner has signed it**. | Zero P1 open beyond SLA at pilot close |

### 8.2 Criteria specific to this release — proving the journey actually runs

These exist because §1b is honest: the commercial spine has not yet been exercised past a draft
quotation on the deployed system. Each is a thing the pilot must produce at least once, on
production, with a real record.

| # | Criterion | Evidence that closes it | Target |
|---|---|---|---|
| B1 | A real customer email becomes a Lead with its source document attached, without manual repair. | The Lead, its source evidence, and the Inbound mail decision row | **≥ 20** distinct enquiries |
| B2 | A Lead is promoted to a formal RFQ through a recorded participation decision. | The RFQ carrying its originating Lead reference | **≥ 10** |
| B3 | A supplier RFQ is sent to a real supplier from the tenant's own mailbox, and a supplier quotation is captured back. | Supplier quote inbox record with the supplier's document | **≥ 5** |
| B4 | **A customer quotation is sent to a real customer**, from the tenant's own address, with the price-source attestation recorded. | Quote in **Sent** state, attestation record naming who confirmed and the source | **≥ 5** |
| B5 | A client purchase order is received, uploaded and matched to its quotation at line level, with award classification. | Matched client PO with full / partial / not-awarded lines | **≥ 1** |
| B6 | A supplier purchase order is raised against a customer-demand award and carries its RFQ, Quote and Sales Order references. | The supplier PO with its reference chain intact | **≥ 1** |
| B7 | The three roles behave as designed under real use: a Sales Representative cannot reach the Users screen or manager authority; a Sales Manager can govern the team without platform access. | Role verification signed off by the Tenant Owner | Pass |
| B8 | **A restore rehearsal is completed** and its measured RPO and RTO are written into §7.2. | Signed rehearsal record | Pass |
| B9 | External uptime monitoring is live and alerting to the named support contact (§5.4). | A test alert delivered and acknowledged | Pass |
| B10 | Zero unplanned production outages longer than 30 minutes during Saudi business hours in the final pilot month. | Incident log | Pass |

### 8.3 Explicitly carved out of pilot acceptance — ZATCA

The BRD's fourth pilot-success criterion — *"at least one successfully generated ZATCA-compliant
e-invoice through Nexora"* — is **removed from this pilot's acceptance test.**

- **Reason:** decision R1. ZATCA is sequenced last and there is no e-invoicing code in this release.
- **Effect:** this pilot cannot be failed on it, and it cannot be claimed as passed either.
- **Where it goes:** it is deferred to a **ZATCA acceptance milestone**, separately scoped and
  separately signed, targeted for **____ / ____ / 20____** and gated on three things the client must
  supply or decide: the standard-versus-simplified invoice path (**E28**), Tech Connect's ZATCA
  onboarding status and production CSID (**E29** — recorded as the single longest external lead time
  in the programme), and the ZATCA-versus-ERP authority boundary (**D5**).
- **Prerequisite the client should know about now:** an auditable period tax register and a supplier
  tax-invoice record (**E48**, **E49**) are prerequisites for a defensible VAT position and are not in
  BRD v3.0. They need written approval to be built. **Every purchase made before that lands is a
  period whose input VAT cannot be evidenced.**

### 8.4 Not committed in this pilot

For the avoidance of a later dispute, the following BRD non-functional targets are **not** acceptance
criteria for this pilot, for the reasons given in §2: 99.5% availability (§2.4); RPO ≤ 24 hours and
RTO ≤ 8 hours (§2.5, until rehearsed); MFA on every login (§2.6); Saudi data residency (§2.3); full
Arabic/RTL interface and Hijri calendars (§2.2); two-year history migration (§2.9).

### 8.5 Sign-off

| | Name | Signature | Date |
|---|---|---|---|
| Tenant Owner (client) | ____________ | ____________ | ____ / ____ |
| Kodekinetics delivery lead | ____________ | ____________ | ____ / ____ |

---

## 9. Open decisions the client must make

Each of these is a business decision. Engineering will not guess any of them. They have lead time,
so deciding late is the expensive option — the cost is paid in calendar days at the end of the
project, when days are worth most.

| # | Decision | Why it cannot wait | Owner | Needed by | Decided |
|---|---|---|---|---|---|
| 9.1 | **Data residency.** Accept US hosting with a documented and legally approved PDPL transfer mechanism, **or** fund re-platforming into KSA-resident infrastructure (the client's own environment per decision R2, or STC Cloud / Oracle Jeddah / AWS Bahrain). | None of Render, Vercel or Neon offers a Saudi region, so this is not a setting — it is a programme with weeks of lead time. It blocks compliance sign-off either way. Records: **E38**, **R2**. | Client legal + IT | Before pilot data becomes real customer data | ☐ |
| 9.2 | **ERP platform and integration contract.** Which ERP becomes the system of record in Phase 2, and when the integration is contracted. | Nexora operates standalone in Phase 1 by design, but the master-data governance model and the posting boundary for supplier POs, sales orders, deliveries and invoices depend on the answer. Record: **D3**. | Client IT + Finance | Before Phase 2 scoping | ☐ |
| 9.3 | **Carriers and freight forwarders.** Which subset is in scope for Phase 1. | Live carrier API integration is out of scope, and the manual tracking path is complete with a marked seam for an adapter. Naming the carriers now means an adapter is a small addition later rather than a schema change. Records: **D4**, **R26**. | Client logistics | Before fulfilment is exercised | ☐ |
| 9.4 | **Retention and deletion policy.** How long source documents, commercial records and personal data are kept; what authorised disposal looks like; what triggers a legal hold. | The retention and legal-hold machinery is built (§7.3) but has no policy loaded into it. Immutable RFQ source retention and a deletion obligation pull in opposite directions and only the client can resolve which wins. Record: **D6**. | Client legal + Tenant Owner | Before pilot close | ☐ |
| 9.5 | **Currency.** Confirm the functional currency the tenant trades in (the wizard defaults to **SAR**), and acknowledge that Kodekinetics' own billing currency is a platform constant in **USD** and is a separate thing. Confirm whether foreign-currency supplier quotations must be converted, and on whose rate. | Currency is set at provisioning and touches every quotation. Changing it after live quotations exist is not a settings change. Record: PR #141 item C. | Tenant Owner | At setup (§4.4 item 2) | ☐ |
| 9.6 | **Sender domain.** Confirm the mailbox and domain quotations are sent from, and complete the SPF/DKIM records for it. Confirm the two behaviours you are accepting: the From display name on tenant sends is the **business unit name**, and **pausing your mailbox falls back to the Nexora platform address** rather than stopping sending. | As of this release, a tenant with its own configured mailbox sends from it, and a tenant without one falls back to the platform address (PR #141). Without your DNS records in place, mail from your own domain is far likelier to be filtered — and a quotation in a customer's spam folder is a lost bid, not an IT ticket. | Client IT | At setup (§4.4 item 3) | ☐ |

Two further decisions are Kodekinetics' to fund but affect what the client can hold us to, so they
are listed for visibility: multi-instance hosting (which is what makes §2.4's availability target
reachable), and the uptime monitoring commitment dated in §5.4.

---

## Appendix A — Release identity

| | |
|---|---|
| Backend commit | `b496e2e` |
| Frontend commit | `b496e2e` (Vercel production alias) |
| Verify backend identity | `GET https://nexora-fyjw.onrender.com/build-identity` must report the same commit |
| Readiness | `GET https://nexora-fyjw.onrender.com/ready` — 11 of 11 checks healthy at issue |
| Backend hosting | Render, region `oregon` (US), single instance, 5 GB persistent disk at `/var/data` |
| Database | Neon PostgreSQL, AWS `us-east-1`, project `Nexora-neon-cyclamen-house` |
| Frontend hosting | Vercel |
| Malware scanning | ClamAV as a private service; both a clean scan and an EICAR detection control pass on every readiness check |
| Included in this release | PR #140 (backend hygiene: seeded reference lists, inspected uploads, gated back-fill, traceable logs), PR #141 (per-tenant sender, tenant invitations, currency separation), PR #142 (token revocation, object-store cutover behind a flag), PR #143 (day-one usability) |

## Appendix B — Source documents

| Document | What it holds |
|---|---|
| `docs/nexora/releases/pilot-closure-2026-08-30.md` | The frozen closure gate list this document closes the last item of |
| `docs/GATE9_10_READINESS.md` | What is engineering and what needs the client's money or signature |
| `docs/NEXORA_BRD_V3_CONTROLLED_EXTRACT.md` | The controlled requirements extract, including §8 pilot acceptance |
| `docs/NEXORA_PHASE1_DECISION_REGISTER.md` | Ratified decisions R1–R28 and the open register D1–D12, E28–E49 |
| `DEPLOYMENT.md` | Environment configuration, provisioning and the bootstrap path |
| `docs/RUNBOOK-RESTORE.md` | Restore procedure and rehearsal record (in preparation, §7.2) |
| `render.yaml` | The reviewed desired-state hosting contract |
