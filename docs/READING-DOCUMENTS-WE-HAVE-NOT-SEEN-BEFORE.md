# Reading documents we have not seen before

**Written:** 2026-09-09, after the batch reconciliation failure of 2026-09-08 (job 416, BU 7).
**Status of the code:** the refusal path described here is built. The adaptive reading path is
**not**, deliberately — see "What is not built, and what must happen first".

This page exists because the reasoning below is worth more than the patch that came out of it, and
a patch is the only part that normally survives. The failure was small. The thing it exposed —
that intake decided what a document *was* by looking at its column headings — is not.

---

## What happened

A client sent a spreadsheet to test us. 2,241 rows, four columns:

| ASMO Item# | ARAMCO Item # | MSG Descreption | MSG # |
|---|---|---|---|
| 2000008634 | 1000720728 | Batteries (053000) | 53000 |

Every row read perfectly. No header matched an alias, so the deterministic mapper returned zero
rows and the document fell through to the model path. It cleared the external-provider gate — the
tenant *does* have a provider authorised — and was refused by the pre-flight cost ceiling: 102
chunks for 2,224 detected items against a limit of 30. That refusal repeated **five times**, then
dead-lettered.

The operator was told: *"We could not read this document… the usual cause is a scanned or PDF
document that needs AI reading, which is switched off for this tenant by default."*

Four things wrong in one screen. We had read it completely. AI was not switched off. Enabling it
would have changed nothing, because the ceiling is reached before any provider is consulted. And
the advice — upload it as a spreadsheet — was given to somebody who had uploaded a spreadsheet.

**The document is not an enquiry.** It is an item cross-reference: the seller's numbering mapped
to the buyer's. No line states a quantity, a price, a unit or a date. Nothing in it can be quoted
at any price. The refusal was right; everything we said about it was wrong.

---

## The principle

> **Decide what a document is from its CONTENT, not from its column headings.**

Header matching asks "do I recognise this spelling?" That is a question about our alias list, not
about the document. Content asks "is there anything here that could be quoted?" — which is the
question that actually matters, and the only one whose answer does not depend on who is asking.

This is not a refinement of header matching. It is the layer above it. Headers remain a useful
*hint*; they must never be the *gate*.

### What content-first survives that header matching does not

- a client renaming their columns
- a client reordering their columns
- two clients sharing an identical header row and meaning different things by it
- a sheet with no header row at all
- headers in a language nobody wrote aliases for

A ten-digit code is an identifier in every language. A date is a date. That is why this approach
adapts to change and alias lists never will: **an alias list only ever knows what it was told.**

---

## Rules that follow, and the reason for each

**1 · Position is never part of any key.**
Column E is Quantity for one client and Manufacturer for the next. A remembered mapping keyed on
position reads quantities out of a manufacturer's name, silently, and produces a quote that looks
perfectly correct. Key on header *text* at most; on content shape by preference.

**2 · Never apply a remembered mapping without re-checking it against the data.**
Two clients can share a header row and mean different things — `Code` holding part numbers for
one, category codes for another. Memory is an accelerator that may go stale, never an authority.
If memory says a column is a quantity and the column now holds text, discard the memory and treat
the layout as new.

**3 · Refusing needs less confidence than assigning.**
"No column anywhere carries commercial shape" is a claim about absence and is easy to be sure of.
"*This* column is the unit price" is a guess that reaches a customer's quote and is wrong by an
order of magnitude when it misses. Build the negative check first and trust it further. This
asymmetry is why the shipped work is a floor and not a classifier.

**4 · Decline to judge when the evidence is thin.**
A genuine two-line enquiry cannot support a column profile. Refusing it would throw away real
business on no evidence, and there is nothing to save by trying — a handful of lines is one chunk.
Below five data rows the floor says nothing and the document takes the normal path. *Absence of
evidence is not evidence of absence.* Four existing reader tests caught this; one of their
fixtures is exactly such an enquiry.

**5 · Magnitude, not type, separates a quantity from a reference.**
Three of the four columns in the document above are numeric. Ten-digit material numbers and
five-digit group codes are told from order quantities by their median value, not by their type. A
naive "is there a numeric column?" floor would have called that cross-reference an enquiry.

**6 · No customer's name belongs in the product.**
Aliases for `ARAMCO Item #` were written during this work and deliberately dropped. A customer
name in a global map that runs for every tenant is the narrowing we are trying to avoid, it does
not generalise — the next client writes `Item Code`, `Cust Ref`, `Matl No` — and it would have
turned this document into 2,241 lines with no quantities, which is worse than a clean refusal.
The codebase already carries Aramco-specific template files; that is a pre-existing decision, not
a precedent to extend.

**7 · Ask for confirmation, never for instructions.**
"Which column is the quantity?" is an admission that the app cannot read. "I read column E as
Quantity — keep that for next time?" is a system that did the work and wants a nod. Same dialog,
opposite impression. The app must always arrive with an answer.

**8 · Learn from corrections, silently.**
Do not ask "shall I remember this?". If an operator corrects an inferred field, record it and
apply it next time. Learning that is invisible reads as intelligence; learning that must be
instructed reads as dictation.

**9 · A non-bid must never reach the Leads page.**
The verdict belongs at intake. Surfacing unmapped columns *on a lead* means the lead already
exists — the junk is already in. Whatever the interaction, it happens before anything is created.

---

## Why this is margin, not polish

Extraction is the only step in the commercial spine that costs money; every other stage runs at
zero model calls. Measured against the cost table, a full chunk is ~11,525 tokens, so the document
above would have cost **~1.2M tokens per pass** to extract quantities that do not exist.

The ceiling saved that — but only by accident, because the file was large. **Trimmed to 50 rows
the same document clears the ceiling, reaches the model, and becomes a lead** whose every line is
flagged "Quantity is required and the document states none." Small garbage already becomes leads;
we simply have not hit it yet. A content floor fires at 50 rows and at 2,241 alike, for the right
reason rather than a cost accident.

---

## What is built

- `SpreadsheetBidEvidence` — profiles column content and answers whether anything is quotable.
  Four independent signals (quantity, price, unit, date) must **all** be absent to refuse.
- `NotABidDocumentException` — read completely, understood, and not an enquiry. Deliberately not a
  parse failure: every sibling exception means "we could not read this."
- Category `NOT_A_BID_DOCUMENT`, permanent on the first attempt, retry disabled, its own operator
  prescription pointing at master data.
- The cost ceiling reports as permanent and classifiable instead of retrying five times.
- Refusal copy is selected by the marker inside the recorded reason, because on PostgreSQL a
  database trigger owns `error_code` and stamps the same bucket value on every dead letter.

## What is not built, and what must happen first

Positive field inference, header fingerprinting, mapping memory, the confirm-and-remember
interaction, and re-verification of memory against content.

All of it is sound in principle and none of it is justified by evidence yet. **This entire
conversation was driven by one document that turned out not to be an enquiry at all.** Before
building an adaptive mapping layer, count how often a *genuine* enquiry actually fails header
matching in production:

```
"was read but no RFQ column layout was recognized"
```

That log line already exists. If it is rare, the mapping layer is speculative work. If it is
common, the count tells you which layouts to design against. Querying this system has been
reliable; reasoning about it has not.

## Open risks

- **The ~650-line ceiling is untouched.** A genuine large enquiry from any client still
  dead-letters at roughly 650 detected items — it now fails honestly rather than confusingly. This
  is lost orders, not a confusing screen, and it is the largest remaining gap. The fix is to make
  large spreadsheets reach the deterministic path, not to raise a model ceiling.
- **The quantity magnitude threshold (median > 10,000 means "reference, not count") is tuned, not
  derived.** Cable ordered by the metre trips it; a unit or date column independently rescues such
  a document, which is why four signals are required rather than one. Worth revisiting against
  real bulk enquiries.
- **The content floor covers spreadsheets only.** PDFs, Word prose and email bodies are unaffected.
