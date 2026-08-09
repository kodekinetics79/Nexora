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
**Status:** built and wired on `customers.list`, `leads.list` and `suppliers.list`. **The one grid
he most likely pictured — the RFQ line grid (`lead.items`) — is still API-only.** See "What is
still owed" below.

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

### What was built (2026-08-09)

Per-user column preferences: visible set and order, scoped to `(business unit, user, view)`, with
defaults declared in code rather than seeded. A stale or unknown column key is dropped on read and
pruned on write, and a corrupt payload degrades to the code default — views evolve, and a stored
preference must never be able to break someone's grid. User identity comes only from the token, so
no request can read or write another user's preferences.

Custom fields use one `jsonb` bag per record plus the tenant-scoped definition table. **No
per-tenant DDL** — that would have wrecked both the migration story and row-level security. The
build connected the pre-existing orphaned `custom_field_definitions` subsystem rather than growing
a second one, which is what the section above anticipated. Custom fields appear in the same picker,
badged, and hidden by default so one tenant defining a field never rearranges anyone else's grid.

A field's key is immutable once created, enforced at `SaveChanges` rather than by a disabled input,
because renaming it orphans every value already stored under it. Retiring preserves values and
merely stops offering the field. Changing a populated field's data type is refused outright rather
than silently coercing.

### What is still owed

- **`lead.items` — the RFQ line grid — has no UI.** It is in the catalog and reachable through the
  API, but no grid consumes it. This is the grid the request was actually about: "additional fields
  … in the lead". Until it is wired, the highest-value half of AA-01 is not delivered.
- The three enrichment candidates listed above — inventory context, commercial memory, extra
  document columns — are **not** yet offered as columns. Reordering is done; the thing worth
  reordering is not.
- `Timestamp`, `Json` and `Reference` field types are storable and validated but absent from the
  admin picker, so a tenant cannot actually create one.
- Custom-field columns are not sortable or filterable — no server-side `jsonb` predicate is wired.

### Capability deliberately given up, and why it is recorded here

Retiring the legacy EAV write path in favour of the `jsonb` bag was a deliberate call: two
unsynchronised stores for one concept diverge, and only the timing is in question. It cost four
behaviours that now exist nowhere — conditional rules enforced **on write** (they are still
reported on read, so a field the rules mark read-only can still be written), per-value optimistic
concurrency, idempotency-key replay, and **per-value change history**.

The audit loss is the one that matters, and it is deliberately *not* being fixed in isolation.
Custom fields on a Customer are now editable with no before/after trail — but so are that
customer's ordinary fields, because **E44** already records that the Customer, Supplier and Product
controllers write no audit events at all. Fixing custom-field auditing alone would produce the odd
result that a tenant-defined field is better audited than the customer's own credit limit. The
right fix is E44 as a whole, and it is awaiting a decision.
