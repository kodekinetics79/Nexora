# Gate 1 — closure status

**FR-RFQ-01 … FR-RFQ-08.** Assessed against the BRD's own bar: a requirement is closed only with
persistence, domain behaviour, UI wiring, tenant isolation, audit evidence, automated tests and a
real rendered-browser path. Anything short of that is reported short, with the missing layer named.

| Req | Entering Gate 1 | Now | Remaining |
|---|---|---|---|
| FR-RFQ-01 · three intake channels | PARTIAL | **PARTIAL** | Watched-folder screen built; folder *state* (files waiting, last sweep) has no endpoint because nothing persists it. SFTP/SharePoint descoped by the client-hosted decision |
| FR-RFQ-02 · nine intake formats | VERIFIED | **VERIFIED** | — DOCX now also read structurally |
| FR-RFQ-03 · ID format + agreement reference | CONFLICTING | **PARTIAL** | Column added and deviation approved; nothing populates it yet |
| FR-RFQ-04 · bilingual OCR + eleven fields | PARTIAL | **PARTIAL** | Hijri and closing time done; delivery location and required delivery date have columns but no extraction; Arabic deferred by approved deviation |
| FR-RFQ-05 · amendment versions the RFQ | PARTIAL | **PARTIAL** | A closing-date-only amendment still goes to human review rather than auto-versioning |
| FR-RFQ-06 · duplicates held before creation | CONFLICTING | **CLOSED** | — |
| FR-RFQ-07 · routing rules in master data | PARTIAL | **PARTIAL** | Admin screen built; Territory and Key-account-team scopes are dormant in the engine, so "region rules" is only partly satisfied |
| FR-RFQ-08 · immutable, auditable source | PARTIAL | **PARTIAL** | Digest now recorded; verification-on-read not built |

**Gate 1 is not closed.** One of eight requirements is fully closed, one was already verified, and
six advanced materially. Declaring it closed would be exactly the kind of claim this audit exists
to prevent.

## What closed, and why it counts

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

Four items surfaced while building the two missing screens. All are recorded rather than fixed,
to keep this increment inside its scope.

0. **Territory and Key-account-team routing rules can never fire.** The engine derives scope keys
   for `Branch` and `ProductCategory` only, so a rule written against either of the other two
   scopes matches nothing on an ingested RFQ. The requirement names *region* rules explicitly, so
   this is the substantive half of FR-RFQ-07 still outstanding — the configuration surface now
   exists, but the engine cannot act on two of its six scopes. The screen says so inline rather
   than letting an administrator create a rule that silently never applies.

1. **The mailbox polling interval contradicts itself.** The stored default is `300` and
   `EmailBackgroundService` reads it with `TimeSpan.FromSeconds`, but the mailbox health text, the
   DTO default of `5` and the mailbox screen all describe it as **minutes**. An operator who sets
   `5` expecting five minutes gets a five-second poll. Whichever unit is intended, three surfaces
   currently state the wrong one.
2. **Folder-sourced inquiries cannot be filtered.** The All Inquiries lead-source filter offers
   Email, Manual and Bulk only, so the three watched-folder sources are invisible to it.

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
