# Neon / PostgreSQL setup (branch: `postgres-migration`)

Status: **the app is ported to Npgsql and runs against Neon.** Verified —
`dotnet ef database update` applied the full schema to Neon, and the running API
returns `/health` = `Healthy` (a live DB check), with EF queries executing
against Neon. Remaining: seed reference data (see below).

## Endpoints (Neon)

Neon gives two hostnames for the same database:

| Use | Endpoint | Why |
|---|---|---|
| **Migrations / DDL / bulk load** | **direct** (`ep-...<id>.<region>.aws.neon.tech`) | DDL + long transactions don't work through PgBouncer transaction-mode pooling |
| **App runtime (at scale)** | **pooled** (`ep-...<id>-pooler.<region>.aws.neon.tech`) | serverless connection pooling; requires `Max Auto Prepare=0` and RLS via `SET LOCAL` in a txn (see ADR-0005) |

The connection string is **never committed** — it lives only in the git-ignored
`Backend/ERP_RFQ_Automation/appsettings.Development.json`, user-secrets, or the
container host's environment. `appsettings.json` ships placeholders only.

Npgsql key-value form:
```
Host=<direct-endpoint>;Database=neondb;Username=neondb_owner;Password=<secret>;SSL Mode=Require;Trust Server Certificate=True
```

## Apply the schema
```bash
cd Backend/ERP_RFQ_Automation
export PATH="$PATH:$HOME/.dotnet/tools"
ASPNETCORE_ENVIRONMENT=Development dotnet ef database update \
  --connection "Host=<direct-endpoint>;Database=neondb;Username=neondb_owner;Password=<secret>;SSL Mode=Require;Trust Server Certificate=True"
```

## Run the app against Neon
Set `ConnectionStrings:DefaultConnection` (Development.json / env) to the Neon
connection, then `dotnet run`. Confirm: `curl http://localhost:5192/health` → `Healthy`.

## Remaining: seed reference data (follow-up)

The app connects and the schema is complete, but the tables are empty, so login
needs the reference rows (`BusinessUnits`, `Users`, `Setup_Master`,
`RolePermissions`, `Module`, `Currency`, `setUOM`, `SetCountry/State/City`,
`Teams`, `Warehouses`, `QuoteConfiguration`). A full clone of the old SQL Server
data lives in `scratchpad/nexora.bacpac` and in the local SQL Edge container.

Two known frictions when copying SQL Server data into managed Postgres:
1. **Schema names:** source is `dbo.*`, the EF schema is `public.*` — remap.
2. **FK ordering** on a non-superuser DB (Neon's `neondb_owner` can't
   `disable triggers`) — load parents before children, or defer.

Recommended approaches (pick one):
- **pgloader** with `--no-ssl-cert-verification`, a `dbo`→`public` schema remap,
  `WITH data only, quote identifiers`, loading reference tables in FK order.
- **Local-first:** load SQL Edge → local Postgres (superuser, `disable triggers`
  works), verify, then `pg_dump --data-only` → load to Neon in dependency order.
- **Targeted seed:** for the pilot, script INSERTs for just the login-critical
  tables (BusinessUnits → Setup_Master → Module → Users → RolePermissions).

## Local dev databases (this machine, for iteration)
- SQL Server clone (old data): Azure SQL Edge container `nexora-sql`, `localhost:14330`.
- Postgres 16: container `nexora-pg`, `localhost:55432`, db `nexora` (schema applied).

## Open items from the port (tracked)
- Recreate the keyless view `View_SupplierPriceList` via a raw-SQL migration
  (EF does not emit views).
- Reconcile the mapped `Taxis` table name (came through as `Taxes`).
- Phase-0 multi-tenant work (Tenant model, EF global query filters, RLS) — ADR-0005.
