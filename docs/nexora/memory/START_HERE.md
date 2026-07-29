# Nexora Repository Memory

This is the short entry point for incremental engineering work. Read it before opening broader release records.

## Active system

- Frontend: React 19, TypeScript, Vite, MUI, TanStack Query in `Frontend/`.
- Backend: ASP.NET Core 8, EF Core/Npgsql in `Backend/ERP_RFQ_Automation/`.
- Database: PostgreSQL/Neon with tenant query filters, transaction-local tenant context, and RLS.
- Approved topology: Vercel frontend, Render backend, Neon PostgreSQL.

## Durable sources of truth

1. `../AGENTS.md` from the active repository workspace root
2. `docs/nexora/capability-register.md`
3. `docs/nexora/current-architecture.md`
4. `docs/nexora/v1-completion-matrix.md`
5. Current release certification and evidence records under `docs/nexora/`
6. Checked-out code, migrations, and tests when documentation disagrees

## Non-negotiable lineage

One immutable Nexora Serial carries customer, contact, owner, source evidence, and commercial history through Lead, RFQ, Quote, follow-up, Order, procurement, shipment, invoice, and payment. Tenant identity always comes from authentication and remains enforced in HTTP, EF, composite keys, and PostgreSQL RLS.

## Document intake checkpoint

The governed path is immutable quarantine, security scan, cleared evidence, local parser/OCR, extraction, and reconciliation. Scanner unavailability is a recoverable `AwaitingSecurityScan` state. Retrying blocked files must use the verified stored object and the original occurrence; it must not require re-upload or create duplicate extraction jobs, Leads, RFQs, costs, or KPI contributions. Confirmed malware and invalid document envelopes remain terminal and fail closed.

## Incremental context refresh

- Start with `git status`, current HEAD, and the files named by the active task.
- Use the capability and architecture records to identify the owning module before searching.
- Read only the relevant controller/service/entity/migration/test/UI slice.
- Update this file only when stack, topology, invariants, or primary source locations change.
- Never reread historical prompt archives to reconstruct current behavior.

## Required verification

Run focused tests first. Before a releasable increment, run the complete portable and PostgreSQL lanes, backend build, frontend lint and production build, model-drift check, security/authorization tests, browser SIT, and `git diff --check` as applicable.
