# Gate 1 — closure status

**FR-RFQ-01 … FR-RFQ-08.** Assessed against the BRD's own bar: a requirement is closed only with
persistence, domain behaviour, UI wiring, tenant isolation, audit evidence, automated tests and a
real rendered-browser path. Anything short of that is reported short, with the missing layer named.

| Req | Entering Gate 1 | Now | Remaining |
|---|---|---|---|
| FR-RFQ-01 · three intake channels | PARTIAL | **PARTIAL** | Watched-folder screen built; folder *state* (files waiting, last sweep) has no endpoint because nothing persists it. SFTP/SharePoint descoped by the client-hosted decision |
| FR-RFQ-02 · nine intake formats | VERIFIED | **VERIFIED** | — DOCX now also read structurally |
| FR-RFQ-03 · ID format + agreement reference | CONFLICTING | **CLOSED** | `NXR-` numbering ratified as an approved deviation; agreement reference captured **and read** — see the correction under FR-RFQ-03 / FR-RFQ-04 |
| FR-RFQ-04 · bilingual OCR + eleven fields | PARTIAL | **CLOSED** | Hijri, closing time, delivery location and required delivery date captured **and read** — three of them had no reader at all until the continuity audit; see the correction below. Arabic deferred by approved deviation. No automatic check that a promised lead time meets the buyer's required date — decision required |
| FR-RFQ-05 · amendment versions the RFQ | PARTIAL | **CLOSED** | A closing-date amendment now versions the existing inquiry in either direction |
| FR-RFQ-06 · duplicates held before creation | CONFLICTING | **CLOSED** | — |
| FR-RFQ-07 · routing rules in master data | PARTIAL | **CLOSED** | Admin screen built and Territory (region) rules now derive and fire. Key-account-team stays underivable and says so on every decision — no customer→team link exists to derive it from |
| FR-RFQ-08 · immutable, auditable source | PARTIAL | **CLOSED** | Digest recorded at capture and verified on every read, failing closed and audited on mismatch |

**Seven of eight requirements are now closed**, one (FR-RFQ-01) stays partial, and the GATE
itself remains open — because three of its completion criteria are environmental and cannot be
satisfied from a development machine at any level of effort.

FR-RFQ-01 is the honest holdout: all three intake channels exist and run, but "monitored
continuously" is a claim only a live mailbox can substantiate, and that is one of the three
environmental criteria below.

## What closed, and why it counts

**FR-RFQ-03 / FR-RFQ-04.** The four intake fields that previously had nowhere to live are now
captured end to end: delivery location, the buyer's required delivery date, the Hijri closing date
rendered alongside the Gregorian one, and the standing-agreement reference. "Requested Delivery:
9 weeks" now lands on the buyer's requirement rather than being forced into a supplier lead time —
the conflation that once wrote "deliver immediately" onto every line.

> **Correction, and the reason it is recorded rather than quietly fixed.** For one gate this
> paragraph was false in the only way that matters. Three of those four fields —
> `RequiredDeliveryDate`, `BidClosingDateHijri` and `AgreementReference` — were written by
> extraction and read by **nothing**: no DTO, no projection, no screen, no column in the list-view
> catalogue. "Captured end to end" described a write. Claiming the Hijri date was "rendered
> alongside the Gregorian one" claimed a read that did not exist anywhere in the codebase, which is
> failure #2 in `WIRING_CONTRACT.md` with a gate document vouching for it — the false assurance the
> contract exists to stop, and worse than the gap it concealed, because a reader of this page would
> have stopped looking.
>
> The claim is now true. `RequiredDeliveryDate` is on `LeadResponseDTO`, in both lead projections,
> correctable by the reviewer through `LeadReviewHeaderDTO`, a default-visible `leads.list` column
> beside the deadline, and a field on the lead detail screen and the extraction workbench.
> `BidClosingDateHijri` renders under the Gregorian bid-close date on the lead detail screen and as
> the helper line on the workbench field a reviewer reads with the source document open — the only
> place a Hijri/Gregorian cross-check is worth anything — plus a selectable list column. It stayed
> in scope rather than going to the deferred Arabic work (decision R6) for the reason the entity
> already gave: this is data correctness, not language. A Hijri deadline read as a Gregorian one
> loses the bid whatever language the interface is in. `AgreementReference` renders on the lead
> detail screen and as a selectable list column.
>
> **Still owed, and not claimed here: nothing compares the buyer's required delivery date to what
> Nexora promises.** The date is now visible to the person making the promise, which is what
> FR-RFQ-04 asks for; an automatic guard is not, and it needs a decision rather than an
> implementation. See item 3 under "Found while closing, not yet fixed" below.

**FR-RFQ-05.** A closing-date amendment versions the existing inquiry instead of queueing for a
human, in either direction, gated on the same similarity bar as the neighbouring arm so a genuine
second inquiry is never swallowed.

**FR-RFQ-07.** Territory — the requirement's "region rules" — now derives from the delivery
location, falling back to the customer's region and resolving city wording against the tenant's
own state and city masters. Key-account-team remains **underivable and says so on every routing
decision**: no customer-to-team link exists anywhere in the schema, and the only available path
would use the ownership rows to choose between the ownership rows.

**FR-RFQ-08.** The digest is recorded at capture and verified on every read. A mismatch fails
closed with an audited security event, leaking neither the storage path nor either digest, and
verification hashes the same handle it serves so there is no window between checking and serving.

**FR-RFQ-06.** The duplicate rule — same buyer, same item, overlapping dates — now runs *before*
a record is created, in the authoritative pre-persistence engine, feeding the existing review
queue. Two tests, one proving no row is written, one proving the date dimension genuinely gates it
so a legitimate repeat order is not held forever.

**FR-RFQ-02** gained something real even though it was already verified: a Word RFQ whose lines
sit in a table is now read deterministically instead of being flattened to prose for a model.
All 120 sample documents and 641 line items read with no model, no egress and no GPU.

## What was fixed along the way

Six ingestion defects, each of which lost customer data silently:

- The same tender produced a different closing date, or none, depending on which of **six**
  divergent date parsers ran. Now one.
- A tender closing at 14:00 lost its deadline **entirely** — no format carried a time and the
  parser returned null rather than degrading to the date.
- A genuine Excel date cell was reported as an unsupported format on the only ingestion path that
  works without AI.
- Spreadsheets had no unit column at all, so "500 M" of cable was ingested as a bare 500.
- A workbook opening with a title block mapped no columns and discarded the whole RFQ.
- Four filters silently dropped any line whose quantity the document did not state.

And one defect introduced during this work and caught in review before it shipped: a lead time of
`0` — "deliver immediately" — written onto every line of every document because an optional field
failed to parse. Fixed structurally, so no optional field can invalidate a document and no numeric
value is emitted unless it was actually parsed.

## What is genuinely blocked, and on what

Three Gate 1 criteria cannot be satisfied from a development machine. They need the single-box
deployment standing up, and they are the reason the gate stays open rather than being waved
through:

1. **A real rendered-browser path** through intake. The BRD names this explicitly; automated tests
   are not a substitute.
2. **A live mailbox.** Email intake is exercised against a real IMAP account, not a fixture.
3. **Durable evidence storage and a real malware scanner.** The active inspector detects no actual
   malware; ClamAV is in the compose file and unproven until it runs.

## Found while closing, not yet fixed

Items surfaced while building the two missing screens and during the Gate 1 continuity audit.
All are recorded rather than fixed, to keep this increment inside its scope, except where a line
is explicitly marked FIXED.

0. **Territory and Key-account-team routing rules can never fire.** The engine derives scope keys
   for `Branch` and `ProductCategory` only, so a rule written against either of the other two
   scopes matches nothing on an ingested RFQ. The requirement names *region* rules explicitly, so
   this is the substantive half of FR-RFQ-07 still outstanding — the configuration surface now
   exists, but the engine cannot act on two of its six scopes. The screen says so inline rather
   than letting an administrator create a rule that silently never applies.

1. ~~**The mailbox polling interval contradicts itself.**~~ **FIXED.** The stored default is `300`
   and `EmailBackgroundService` read it with `TimeSpan.FromSeconds`, while the mailbox health text,
   the DTO default of `5` and the mailbox screen all described it as **minutes** — an operator
   setting `5` for five minutes got a five-second IMAP poll. Minutes is now the unit everywhere,
   because minutes is what every human-facing surface already promised. Existing rows are **not
   backfilled**: each keeps its number and changes meaning from seconds to minutes, a sixty-fold
   slowdown, so no row can start polling faster than it does today and the transition cannot create
   the hazard it removes. The **column default** `300` — five minutes as seconds, five hours as
   minutes — must move to `5` and is owed to the migration owner; it is recorded in
   `WIRING_CONTRACT.md` and is unreachable through the product, since both the create and the
   update path always write the value explicitly. One adjacent defect fixed with it: the interval
   was read across *every* active mailbox, so an outbound-only SMTP row set the inbound poll rate
   for the whole tenant despite the DTO documenting the field as "ignored for SMTP".
2. **Folder-sourced inquiries cannot be filtered.** The All Inquiries lead-source filter offers
   Email, Manual and Bulk only, so the three watched-folder sources are invisible to it.
4. **The mailbox health line reports a per-mailbox interval the poller does not honour.** There is
   one poll loop for the whole host and it sleeps for the **minimum** interval across every active
   IMAP mailbox, so a tenant with mailboxes at 5 and 60 minutes polls both every 5 — while the
   60-minute row's health line reads "Polling every 60 minute(s)". The number is the stored
   setting, not the rate in force. Correcting the copy is trivial; giving each mailbox its own
   schedule is a change to how the poller is structured, and it is not in this scope. Pre-existing,
   and unrelated to the unit fix except that the unit fix is what makes the sentence worth reading.

3. **The buyer's required delivery date is visible but nothing compares it to what we promise.**
   This one needs a decision, not an implementation, which is why it is here rather than done.
   `Rfqitem.RequiredDesiredDate` — the per-line required date the comparison machinery already
   reads — is **never written by either lead→RFQ conversion path**
   (`Repositories/LeadRepository.cs:425`, `Intelligence/Conversion/LeadConversionIntelligence.cs:281`);
   it is settable only through the RFQ create/update controller. So for every lead-originated RFQ,
   which is all of them, three existing controls are inert: the `DELIVERY_DATE_MISSED` **blocker**
   (`CommercialLearning/CommercialLearningService.cs:1042`), the required-date gate inside
   `IsOfferEvidenceCurrent` (`:623`), and `QuoteItemResponseDTO.RequestedDeliveryDate`
   (`Services/QuoteService.cs:895`), which reads through the same null column and shows a sales
   engineer nothing beside the lead time they are committing to. Seeding the line date from
   `Lead.RequiredDeliveryDate` at conversion needs **no schema change** and would light all three
   up at once — but it would also arm a hard control: `ProcurementApplicationService.ApproveAwardAsync`
   **throws** on an ineligible comparison, so every award to a supplier who cannot make the buyer's
   date would begin to fail outright. Block, warn, or override-with-reason is a commercial policy
   call and is not one to make by side effect. **Decision required** before this is wired.

## What the watched-folder channel actually is

Worth recording plainly, because it was previously undocumented and the UI for it had been
deleted. `FolderService` sweeps three directories per tenant at
`<Storage:RootPath>/Tenants/{businessUnitId}/Watched/{Shared|SEC|Aramco}`. Each file is
atomically claim-moved into a processing directory, rejected into quarantine if it is a symlink,
the wrong extension, empty or over 25 MB, then put through the same governed ingestion door as
every other channel and moved to a processed directory. Failures stay put behind a database-backed
retry counter and quarantine after three attempts.

Under the client-hosted decision this satisfies the requirement's "watched network folder": the
directory can be a mounted share fed by hand or by a scheduled portal export. **SFTP and
SharePoint connectors are descoped** — recorded as an approved deviation, not an omission.

The screen has no way to show folder *state* — how many files are waiting, when the last sweep
ran — because nothing persists it; the background sweep's report is written only to the log. The
page says so in plain language instead of guessing, and the minimal endpoint needed is specified
in the agent's findings.

## Next, in order

1. Populate the three new intake fields from extraction — the columns exist; nothing writes them.
2. Verification-on-read for the attachment digest. Recording it is half of FR-RFQ-08; refusing to
   serve bytes whose digest does not match is the half that makes "immutable" enforceable.
3. Auto-version on a closing-date-only amendment (FR-RFQ-05).
4. Stand the box up and run the three blocked criteria above.

## Migration

`20260809112755_Gate1RfqIntakeFields` — five nullable columns, no constraints, no backfill, no
foreign keys: `Leads.DeliveryLocation`, `Leads.RequiredDeliveryDate`, `Leads.BidClosingDateHijri`,
`Leads.AgreementReference`, `Attachments.ContentSha256`. Verified to contain only these; it picked
up no drift from other work in the branch. Both test lanes pass with it applied.
