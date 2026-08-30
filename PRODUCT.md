# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Nexora serves tenant-side sales representatives, sales managers, customer-service operators, procurement and sourcing staff, inventory and logistics operators, finance staff, tenant owners, and platform administrators. They use it during live commercial operations to turn customer demand into governed fulfilment and cash collection without losing the source evidence, approval history, ownership, or financial lineage.

## Product Purpose

Nexora converts inbound customer enquiries and documents into canonical commercial work, then carries approved demand through RFQ, sourcing, supplier quotation, customer quotation, order, inventory, shipment, proof of delivery, invoicing, and payment. Success means an operator can complete the real journey without hidden routes or duplicate records, while each consequential transition remains traceable, permissioned, recoverable, and understandable.

## Positioning

Nexora's distinguishing mechanism is a governed evidence-to-cash chain: source email and document evidence is reconciled into one canonical Lead and immutable revisions; explicit fit and participation decisions control RFQ promotion; downstream commercial, inventory, fulfilment, and finance records retain authoritative lineage instead of becoming disconnected module records.

## Operating Context

- Operators work from customer emails, attachments, extracted lines, supplier and customer quotations, purchase orders, warehouse receipts, shipments, POD evidence, invoices, and bank-payment references.
- The primary journey is Email Intake → Canonical Lead → Participation Decision → Formal RFQ → Sourcing → Supplier Quote → Customer Quote → Client PO → Sales Order → Supplier PO → Inventory Receipt and Allocation → Shipment and POD → Invoice and Payment.
- Work is role-scoped and tenant-scoped. Sales representatives act on their assigned commercial scope; managers govern teams and exceptions; finance, procurement, logistics, tenant owners, and platform administrators have distinct authorities.
- Users frequently need to resume interrupted work, understand why an action is blocked, verify evidence, and distinguish a safe retry from a new operation.

## Capabilities and Constraints

- PostgreSQL is authoritative for transactional and governance state. Permitted source documents are evidence, not an alternate source of truth.
- EmailOccurrence capture, document/evidence processing, canonical Lead reconciliation, immutable LeadRevision history, fit assessment, participation decisions, and RFQ Promotion are separate governed stages.
- RFQ Promotion is the only allowed Lead-origin RFQ creation path.
- Full bid, partial bid, and no-bid decisions must remain explicit; only approved lines may be promoted.
- Replays and concurrent retries must not duplicate Leads, RFQs, shipments, PODs, invoices, payments, or legal document numbers.
- Tenant deletion and storage cleanup are governed operations with retention, export, approval, legal-hold, audit, and authorization controls.
- The current implementation is React and Material UI on the frontend and .NET with PostgreSQL on the backend.
- Production must not be modified or contacted during design and local acceptance work unless the user explicitly authorizes it.

## Brand Commitments

- Product name: Nexora.
- Preserve the existing Nexora mark unless a future explicit rebrand replaces it.
- The product should feel credible to an enterprise client at first sight: assured, clear, precise, operationally mature, and free of novelty effects that compete with the work.
- Copy should use the language of the operator's task, explain governed restrictions plainly, and avoid hype or unverifiable commercial claims.

## Evidence on Hand

- Working frontend routes and backend domain implementation in this repository.
- Existing Nexora logo at `Frontend/src/assets/img/logo.svg`.
- Automated domain, PostgreSQL concurrency, permission, component, build, and Playwright acceptance tests.
- Configured non-production test users and seeded synthetic commercial journeys.
- No approved customer testimonials, customer logos, market benchmarks, or performance claims are available and none may be fabricated for the sign-in experience.

## Product Principles

1. Show the next governed action and the reason it is safe or blocked.
2. Preserve evidence, ownership, and lineage across every module boundary.
3. Make safe recovery and idempotent retry visible to operators.
4. Adapt the workspace to the user's authority without hiding the commercial journey.
5. Prefer calm operational clarity over decorative complexity.

## Accessibility & Inclusion

The web application must support keyboard operation, visible focus, readable contrast, clear errors and instructions, reduced motion, responsive layouts, semantic landmarks, and assistive-technology names for interactive controls. The target quality floor is WCAG 2.2 AA for client-facing and authenticated surfaces.
