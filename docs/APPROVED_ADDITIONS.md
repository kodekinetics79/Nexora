# Approved additions to the Phase 1 ceiling

BRD v3.0 is the functional ceiling, and nothing outside it gets built without the product owner's
written approval. This page is that written approval — the short list of things Zack has
explicitly asked for that the BRD does not contain.

Each entry states **when** it is scheduled, because the failure mode this project has already
lived through is not bad ideas; it is good ideas taken mid-gate. Interrupting the transaction
spine for adjacent work is exactly what produced ~63,000 lines of code answering no requirement.

---

## AA-01 · User-configurable line-grid columns

**Approved:** 2026-08-09 by Zack, product owner.
**Scheduled:** after Gate 3 closes, before Gate 4 begins.
**Status:** not started.

### What he asked for

A user ticks which fields they want to see, and reorders the columns to their own preference,
rather than every grid being fixed. In his words: *"that would be configurable like by clicking
the check box the customer will have flexibility to have what field they would think they need
them plus they would be able to shuffle columns as per user preference not a fixed one… I know
this will surely have a high impact from a customer point of view because it gives them freedom
in their hands."*

He also asked, explicitly, that this not be allowed to get buried.

### Why it is cheaper than it looks

Roughly 70% of it already exists and is unreachable:

| Asset | State |
|---|---|
| `Backend/ERP_RFQ_Automation/CustomFields/` | ~1,861 LOC — governance, conditional rules, value validation, typing, save interceptor. **Zero frontend consumers.** |
| `LeadItem.ExtraFields` (jsonb) | Already captures every unmapped column from a customer's document; already renders on lead detail. |
| `ExtractionReviewDetailPage`, `LeadsPage` | Already do ad-hoc column visibility — precedent exists, but per-screen, not persisted, not per-user. |

**Treat the custom-fields subsystem as UNPROVEN, not proven.** A subsystem with no consumers has
never been exercised. Budget for finding defects in it rather than simply wiring it up.

### The part that matters more than the checkbox

Configurable columns are only worth having if there is something worth choosing. The highest-value
candidates are all "data that already exists and the interface throws away":

1. **Extra document columns** already captured per customer. These differ by buyer and are exactly
   what a Sales Engineer wants visible.
2. **Inventory context on the line** — availability, incoming, last purchase price, last supplier.
   The Gate 0 audit found these already present in the API payload and discarded by the component.
3. **Commercial memory** — last won price, win rate for this part. Already computed by the
   learning service and surfaced on only two screens.

Columns you can reorder are a user-experience tweak. Columns that tell a rep what a line is worth
and whether we can supply it change how fast they quote — and speed from inbox to a confident
quote is the product's actual selling point.

### Shape when it is built

One shared column-preference component, persisted per user, applied to the four line grids that
carry commercial decisions: leads, RFQ lines, quote lines, customer PO lines. Plus connecting the
orphaned custom-fields backend so a tenant can define its own fields rather than only inheriting
whatever a document happened to contain.
