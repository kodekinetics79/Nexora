# Nexora

**AI-powered RFQ, sourcing, quotation, and order intelligence platform.**

Nexora receives customer inquiries and RFQs through multiple channels (email,
upload, portal, API), understands the commercial package with source-evidence
tracking, qualifies the opportunity, supports supplier sourcing, calculates
controlled pricing, obtains approvals, generates customer quotations, and
connects the outcome to orders, inventory, and shipment — with continuous,
human-governed learning.

## Architecture at a glance

| Layer | Technology | Notes |
|---|---|---|
| Frontend | React 19 + Vite + TypeScript + MUI 9 + TanStack Query | Data-dense SPA → deploy on Vercel |
| Backend | ASP.NET Core 8 (modular monolith) + EF Core | REST API + hosted workers → container host |
| Database | SQL Server → **PostgreSQL / Neon** (in migration) | `main` on SQL Server; `postgres-migration` branch ports to Npgsql |
| AI / extraction | LLM behind `ILlmProvider` → **Claude** (tiered) | Non-authoritative for money; schema-constrained output |
| Deployment | Vercel (frontend) + container host (backend) + Neon (DB) | See ADR-0004 |

## Decisions of record (ADRs)

All major architectural decisions are documented in [`docs/adr/`](docs/adr/):

- **ADR-0001** — AI provider → Claude (behind a reversible provider seam)
- **ADR-0002** — Technology stack (retain .NET 8 + React 19; independently confirmed by two principal architects)
- **ADR-0003** — Scalable ingestion pipeline (1,000+ docs × 10,000+ line items: durable job queue + workers + chunked map/reduce + bulk insert + idempotency)
- **ADR-0004** — PostgreSQL/Neon + Vercel deployment topology
- **ADR-0005** — Multi-tenant foundation + Platform-Owner control plane (Tenant model, EF query filters + RLS, weighted-fair scheduling, admin console)

The full engineering findings ledger is in
[`docs/PHASE0-FINDINGS.md`](docs/PHASE0-FINDINGS.md), and secrets/rotation guidance
in [`SECURITY.md`](SECURITY.md).

## Repository layout

```
Backend/ERP_RFQ_Automation/   ASP.NET Core 8 API (controllers, services, repositories, EF models)
Frontend/                     React 19 + Vite SPA
docs/adr/                     Architecture Decision Records
docs/PHASE0-FINDINGS.md       Evidence-based findings ledger
SECURITY.md                   Secret handling + rotation
```

## Local development

**Backend** (net8; `dotnet` 8 SDK):
```bash
cd Backend
dotnet build ERP_RFQ_Automation.sln
# Provide real config via appsettings.Development.json (git-ignored) or user-secrets:
#   ConnectionStrings:DefaultConnection, Jwt:Key, Ollama:BaseUrl/ApiKey
dotnet run --project ERP_RFQ_Automation
```

**Frontend** (Node 20+):
```bash
cd Frontend
npm install
npm run dev        # http://localhost:5173
```

> Configuration containing secrets is **never** committed. `appsettings.json`
> ships only `__PLACEHOLDER__` tokens; real values come from
> `appsettings.Development.json` (git-ignored), user-secrets, or environment
> variables. See `SECURITY.md`.

## Status

Under active development. `main` is the hardened, pilot-ready line (SQL Server);
`postgres-migration` is porting the platform to PostgreSQL/Neon as the foundation
for a scalable, multi-tenant SaaS (see ADR-0004 / ADR-0005).
