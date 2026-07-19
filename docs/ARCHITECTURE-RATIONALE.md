# Nexora — Architecture Rationale

*One page on what Nexora is built with, and why each choice was made. Full decision records live in `docs/adr/`.*

Nexora is an AI-powered sourcing platform: it reads inbound RFQ documents in any format, converts them into structured, priced, supplier-ready quotes, and routes every consequential action through human approval. The architecture was selected for one goal: **enterprise-grade reliability on operationally boring technology, with every escape hatch kept open.**

## The stack, and the reasoning

| Layer | Choice | Why | Considered & rejected |
|---|---|---|---|
| Backend | **ASP.NET Core 8 (.NET)** | Top-tier enterprise runtime: performance, static typing, long-term Microsoft support, deep hiring pool. | Rewrite to Node/Python — weeks of risk, zero user-visible gain. |
| Database | **PostgreSQL (Neon serverless)** | No license cost; `jsonb` for flexible customer data; `FOR UPDATE SKIP LOCKED` powering the durable job queue; `citext`, rich indexing; serverless scale-to-zero economics and instant branching. | SQL Server (licensing, weaker serverless story); MySQL (weaker JSON + locking semantics). |
| Frontend | **React 19 + TypeScript (strict) + MUI** | Largest frontend talent pool; compile-time safety; accessible enterprise components that let features ship in hours. | Vue/Svelte rewrite — churn without advantage. |
| Document pipeline | **Durable Postgres-backed job queue + bounded worker pool** | Built for 1,000+ documents per batch: atomic job claims, automatic retries with backoff, per-tenant fair scheduling, poison-document isolation — with zero additional infrastructure to operate. | Redis/RabbitMQ/cloud queues — more moving parts and cost before the throughput demands them; upgrade path preserved. |
| AI layer | **Provider-agnostic LLM interface** (Anthropic Claude-ready; local/OSS models supported) | Models improve monthly; Nexora refuses vendor lock-in. Swapping the reasoning engine is configuration, not a rebuild. Extraction is stateless inference — customer data is never used to train models. | Hard-coding any single provider SDK. |
| Autonomy & safety | **Guardrailed agent engine**: per-tenant policy (autonomy level, value caps), hold-for-approval queue, immutable audit log | Full-autonomy ambition with enterprise controls: every AI action is policy-checked before execution and permanently logged; humans hold override authority at all times. | Unconstrained agent execution. |
| Multi-tenancy | **Shared database + fail-closed tenant query filters + server-enforced RBAC** | Tenant isolation enforced at the query layer and proven by automated tests; role/module permissions enforced server-side, not just in the UI. Database-level RLS retained as a documented upgrade path. | Database-per-tenant now — operational overhead ahead of need. |
| Hosting | **Vercel (frontend) · Render (containerized API) · Neon (data)** | Zero-ops infrastructure appropriate to stage; standard container + standard Postgres = no platform lock-in. | Kubernetes/hyperscaler — complexity before it pays rent. |

## Governing principles

1. **Boring-excellent over novel-fragile.** Every load-bearing component is mainstream, supported, and hireable-for.
2. **Escape hatches everywhere.** Swap the LLM, upgrade the queue, tighten isolation to RLS, move clouds — none of these is a rewrite.
3. **Fail closed.** Missing permission → denied. Unknown AI action → held for approval. Uncertain extraction → routed to human review.
4. **Accountability over blind automation.** Confidence scoring, item-count conservation checks, review queues, and append-only audit trails mean nothing unverified flows downstream.
5. **Decisions are written down.** Every significant choice is an ADR in `docs/adr/` — reviewable, challengeable, and versioned.

## Verification posture

Automated test suite covering tenant isolation, RBAC decisions, guardrail policy, extraction invariants, and business state machines; CI on every push; migrations applied and verified against the live database before release; production endpoints smoke-verified after deploy.

---
*Nexora · The Intelligence Platform — architecture rationale, maintained alongside the code it describes.*
