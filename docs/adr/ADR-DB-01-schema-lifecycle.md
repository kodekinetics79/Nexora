# ADR-DB-01 — Schema lifecycle: where DDL lives, how it is squashed, and how the model is kept honest

- Status: **Proposed** (design; decision requested from the product owner / CTO)
- Date: 2026-08-11
- Deciders: CTO/CIO (orchestrator), Senior Database Architect, Principal Platform Engineer
- Related: ADR-0002 (stack), ADR-0004 (PostgreSQL + deployment), ADR-0005 (multi-tenant
  foundation + RLS), `NEXORA_PHASE1_DECISION_REGISTER.md` R2 (client-hosted deployment),
  commit `06137ef` (the migration squash), `Backend/Dockerfile` (the build-memory record)
- Supersedes: nothing. Establishes policy that did not previously exist.

---

## Context — what is measurably true on 2026-08-11

Every number in this section was measured against this tree, not estimated. Where a figure
comes from the squash certification rather than from a measurement taken for this ADR, it is
marked **[certified]**.

### The build failure that forced the question

`Backend/Dockerfile:16-45` records it. EF Core writes a complete copy of the database model
into every migration's `.Designer.cs` as a single `BuildTargetModel` method — 4,378 lines for
the first migration, 27,579 for the 134th, growing ~174 lines each time. At 134 migrations
that was **2,308,701 generated lines against 208,651 lines of application code: 92% of
everything the compiler read.** Roslyn materialises an `IOperation` tree per method body, so
each copy cost real heap. Render's build pipeline gives every build 8 GB, workspace-wide.
Three consecutive deploys were OOM-killed.

Measured with `scripts/dev/measure-build-memory.sh` against cgroup2 `memory.peak`:

| | peak RSS | time | result |
|---|---|---|---|
| 134 migrations compiled | 5,912–6,030 MB | 113 s | `System.OutOfMemoryException` |
| squashed baseline | 3,866 MB | 23–24 s | passes |

Five other knobs were measured and rejected; `DOTNET_gcServer=0`, the stopgap that had been in
the Dockerfile for weeks, made no difference at an identical cap (6,030 MB with, 5,958 MB
without — both failing). That evidence is preserved in `Backend/Dockerfile:29-45` so it is not
re-proposed.

### Why the squash could not be scaffolded

A regenerated EF baseline would have silently dropped everything the EF model cannot see. The
schema's own catalogue, reproduced independently for this ADR (see *Reproduction*, below):

| control | count |
|---|---|
| tables with RLS enabled | 232 |
| `FORCE ROW LEVEL SECURITY` | 110 |
| policies | 232 (220 of them `nexora_tenant_isolation`) |
| triggers | 300 (32 `ENABLE ALWAYS`) |
| functions | 142 (36 `SECURITY DEFINER`) |
| CHECK constraints | 379 — of which **111 exist only in the database, not in the model** |
| foreign keys absent from the model | 37 **[certified]** |
| indexes absent from the model | 18 **[certified]** |
| column-level ACL entries | 49 **[certified]** |
| `EXCLUDE` constraints | 2 — the only thing preventing double-billing |
| tables created by raw SQL that EF cannot see | 2 |

**138 of the 232 policies are created by a `DO` loop that names no table.**
`Migrations/20260723120000_CompleteTenantRlsCoverage.cs:13-55` sweeps `information_schema` for
any `public` base table carrying a column in
`('BusinessUnitID','BusinessUnitId','business_unit_id','BUID','Buid','buid')` and `EXECUTE
format(...)`s `ENABLE ROW LEVEL SECURITY` + `CREATE POLICY nexora_tenant_isolation` onto each.
43 tables — including `Users`, `RolePermissions`, `Products` and `Inventory` — get their **only**
policy from that loop. Its `Down()` at lines 343-364 is a second sweep guarded by a *hardcoded
16-table exclusion allowlist* that rots silently every time a table is added.

The two tables EF cannot see are `public."FinanceProviderSecrets"`
(`Migrations/20260723230000_GovernStatementsAndDunning.cs:902`) and
`public."LedgerActorNonces"` (`Migrations/20260723235500_CompleteLedgerKernelControls.cs:146`).
Neither name appears in the model snapshot, in `Models/`, or in `Data/`. `dotnet ef migrations
add` will never diff them.

So the baseline **replays a `pg_dump`** rather than transcribing 1,849 statements by hand.
Parity holds by construction. Four clusters produced byte-identical dumps of 40,073 lines
**[certified]**.

### How much of this schema is even EF-shaped

Measured across the 134 pre-baseline migration `.cs` files (excluding `.Designer.cs`), 43,769
lines total:

| | value |
|---|---|
| `migrationBuilder.Sql(` calls | **261** (~2 per migration, ~58 lines each) |
| lines inside raw `Sql("""…""")` bodies | **13,564 (30.9%)** |
| lines consumed by EF model operations | 22,380 (51.0%) |
| migrations containing raw SQL | **105 of 134 (78%)** |
| migrations that are raw SQL *only* | 14 |
| migrations that are EF operations *only* | 28 |
| migrations that are both | 91 |

`CREATE POLICY` appears 124 times across 51 migrations; `ROW LEVEL SECURITY` 251 times across
52; `GRANT` 266 times across 69. **None of it is expressible in the EF model.**

One migration — `20260723021417_SynchronizeProductionModelMetadata.cs`, 27 lines with an empty
`Up()` and `Down()` — exists *solely* because EF cannot tolerate a hand-authored predecessor
that has no generated target model. That file is the tool telling us what it is.

### The divergence EF's own drift check cannot see

`HasPendingModelChanges()` is asserted in the PostgreSQL lane at
`Backend/ERP_RFQ_Automation.Tests/PostgreSqlProductionDialectTests.cs:684`, and it has already
earned its place — its comment at `:672-686` records that "an entire gate's schema — seven
tables across inbound logistics and traceability — reached a green build while goods receipt
was dead on PostgreSQL." The `TestResults/gate8done.trx` run shows 515 failures of which **405
share the `PendingModelChangesWarning` message**: one drift condition, 405 red tests.

But that check compares the **model** to the **snapshot**, and the snapshot is regenerated
*from* the model. The database is built from the baseline SQL, which came from a `pg_dump`.
The database is not in the comparison at all. That is why the model can declare **23
constraints and 9 indexes the database does not have [certified]** while
`dotnet ef migrations has-pending-model-changes` stays silent.

An independent name-level check run for this ADR corroborates that the two artefacts are not in
sync and simultaneously shows why a naive gate will not work: the model snapshot declares 866
indexes; the baseline SQL creates 871 named indexes and 1,327 named constraints; **108 of the
model's declared indexes have no same-named database object** (165 if compared
case-sensitively). Most of those are naming-convention differences rather than missing objects.
**A name-level comparison both over- and under-reports. The gate must compare *shape* — table,
column list, uniqueness, predicate — against a live catalogue.**

### The test lanes

| lane | test classes | test methods | share |
|---|---|---|---|
| PostgreSQL (Testcontainers `postgres:16-alpine`) | 85 | **388** | 11.1% |
| SQLite in-memory | 178 | **1,937** | 55.6% |
| pure unit / model-only | 130 | 1,156 | 33.2% |
| **total** | 393 | **3,481** methods → **4,448** cases | |

There is no EF Core InMemory provider anywhere in the tree. There is also **no skip
mechanism**: zero `[Fact(Skip=…)]`, no `SkippableFact` package, and every historical `.trx`
reports `notExecuted="0"`. CI (`.github/workflows/ci.yml:78-82`) runs `dotnet test` with no
`--filter`, so Docker is mandatory and the PostgreSQL lane genuinely runs — if Docker were
absent, all 388 would fail, not skip. That is the right failure mode and should be preserved.

The SQLite lane builds its schema with `EnsureCreated()` (`Support/TestDb.cs:35`) — **from the
model, not from migrations**. There are 29 `EnsureCreated` call sites and zero `Migrate` calls
on SQLite. The PostgreSQL lane runs `MigrateAsync()` (`Support/PostgreSqlTestDatabase.cs:49`).

Production code contains **136 `Database.IsNpgsql()` branches and zero `IsSqlite()`** — so the
SQLite lane systematically executes the *else* arm of 136 production paths. 19 test sites
switch CHECK constraints off with `PRAGMA ignore_check_constraints` to get SQLite to accept
rows PostgreSQL would reject.

### Deployment constraints that bound every option

- Migrations run **at container start**: `render.yaml` sets
  `Database__ApplyMigrationsOnStartup=true`; `Program.cs:822-844` resolves it and calls
  `Database.MigrateAsync()`. `Program.cs:821` already supports turning it off for an external
  release job.
- Decision **R2** commits to **client-hosted deployment inside the client's KSA
  infrastructure**, with an on-premise artifact and install runbook now in scope. Any migration
  runner must ship inside the existing .NET container and add **no new runtime**.
- EF Core 9.0.9 / Npgsql 9.0.4 on `net8.0`. EF Core 9 takes a migration lock, so multiple
  instances migrating at boot serialise correctly.
- `Microsoft.EntityFrameworkCore.SqlServer` 9.0.9 is referenced
  (`ERP_RFQ_Automation.csproj:54`) but `UseSqlServer` appears **zero times** in the tree.

---

## Decision

### 1. DDL stays in EF migrations — as a *transport*, not as the *source of truth*

**Recommendation: option (b) — EF migrations for model-shaped DDL, raw SQL in versioned `.sql`
files — formalised into the shape the squash has already accidentally built.**

The squashed baseline is already twelve reviewable `.sql` files (40,283 lines, plus a 384-line
`90_down.sql`; 1.7 MB in total) embedded as assembly resources and replayed by an EF migration
whose executable part is ~200 lines and which branches on
`ActiveProvider` (`MigrationsBaseline/20260811033109_SquashedSchemaBaseline.cs:66-100`). That
is the right architecture. It should be made the rule for *new* migrations, not just for the
baseline:

- **EF operations** (`CreateTable`, `AddColumn`, `CreateIndex`, `AddForeignKey`, …) stay in the
  C# half. This keeps the model snapshot tracking them, which keeps `HasPendingModelChanges()`
  working — the guard that already caught a whole gate.
- **Everything EF cannot model** — policies, `FORCE ROW LEVEL SECURITY`, triggers, functions,
  grants/revokes, `EXCLUDE`, partial-index predicates, `NOT VALID` FKs, `SECURITY DEFINER` —
  moves out of C# string literals into a numbered `.sql` file under the migration's own `Sql/`
  folder, embedded as a resource and executed by `migrationBuilder.Sql(ReadScript(...))`.

**Why not (a), status quo plus periodic squash.** The squash is unavoidable under any
EF-based option and is not the problem; the *treadmill* is. See §2 for the arithmetic: at the
observed cadence the new budget guard fires in under six days. Status quo means rediscovering
this every sprint.

**Why not (c), a dedicated migration tool wholesale.** Two objections, one hard and one soft.

- *Hard*: **Flyway needs a JVM; sqitch needs Perl.** Under R2 that means adding a second
  runtime to an appliance shipped into a client's KSA data centre, to be operated by their
  staff. That cost is real and buys nothing on the divergence problem, which is the actual
  defect class. **Grate and DbUp are .NET libraries and would ride in the existing container
  for free** — they are the only credible members of this family here.
- *Soft*: moving wholesale deletes the snapshot, and with it `HasPendingModelChanges()`. The
  SQLite lane would keep building its schema from the model via `EnsureCreated()` with
  *nothing* comparing that model to the database. You would trade a partial, known divergence
  for a total, unwatched one — **unless** the parity gate in §3 exists first.

  This is worth stating plainly, because it is the honest version of the trade-off: **§3 is the
  load-bearing change. Once the parity gate exists, both (b) and (c) are safe, and (b) is
  cheaper today** because the team keeps `dotnet ef migrations add` for the 51% of DDL that EF
  writes correctly, instead of hand-writing every `ALTER TABLE … ADD COLUMN` at a cadence of
  ~6.7 migrations a day.

  So: **adopt (b) now, and set an explicit trigger for revisiting** — *if two consecutive gates
  require an unscheduled squash, move the DDL wholesale into the `.sql` tree and run it with
  DbUp inside the same container.* Structuring the SQL as files today makes that a
  configuration change later rather than a rewrite. This decision is deliberately reversible.

**Why not (d), declarative / state-based (Atlas, migra, pgroll).** Two reasons, both specific
to this schema.

- **A desired-state file cannot express the `DO` loop.** 138 policies are created by code that
  names no table (`CompleteTenantRlsCoverage.cs:13-55`). Adopting a declarative tool means
  materialising all 232 policies explicitly, which silently changes the rule that a new
  tenant table *inherits* isolation. That is arguably an improvement — an explicit policy per
  table is more reviewable — but it is a large behavioural change to a security boundary, and
  it should be taken as its own decision, not as a side effect of picking a tool.
- **Auto-generated DDL against a security boundary is the wrong default.** A state differ that
  decides to `DROP` and re-`CREATE` a policy, or to replace a `SECURITY DEFINER` function, is
  making a security change that nobody reviewed. With 232 policies, 36 `SECURITY DEFINER`
  functions and 49 column-level grants, the blast radius of one bad generated plan is tenant
  isolation itself.
- Atlas additionally needs a "dev database" to compute plans, which is awkward for unattended
  container-start execution and worse on-premise. `pgroll` is the right answer for
  zero-downtime expand/contract *later* (§4) but wants its own runtime and to own the schema's
  views. Not now.

**One defect to fix as part of adopting (b).** The twelve baseline `.sql` files are **not
standalone today** — they only work because EF runs all twelve inside one transaction.
Reproduced for this ADR:

- Applied file-by-file with `psql -f`, **5 of 12 fail.** `02_functions.sql` dies with
  `relation "platform.UsageEvents" does not exist`, because `check_function_bodies` was
  switched off by `SET LOCAL check_function_bodies = false` in
  `01_schema_and_extensions.sql:24` — and `SET LOCAL` does not survive into a new transaction.
  06, 08 and 09 then cascade. `10_explicit_revokes.sql:15` fails on
  `public."__EFMigrationsHistory"`, a table **EF** creates.
- Concatenated into one script, after pre-creating `__EFMigrationsHistory`, and applied with
  `psql --single-transaction`: **succeeds in 3 seconds**, producing rls=232, forced=110,
  policies=232, triggers=300, `ENABLE ALWAYS`=32, `SECURITY DEFINER`=36, `EXCLUDE`=2,
  CHECK=379 — exactly the certified figures.

So the SQL is *nearly* portable and the "reviewable as SQL" property is currently partly
cosmetic. Finish it: each script sets its own GUCs, and the `__EFMigrationsHistory` revokes
move to a file that documents that dependency (or are issued from C#). Cost: ~2 hours. Benefit:
the client's DBA can review and run the DDL, and option (c) stays open.

### 2. Squash policy: scheduled, owned, and verified to a byte

**The budget guard is a tripwire, and the arithmetic says a tripwire is not enough.**

`Backend/Dockerfile:89-104` fails the build at 1,200,000 compiled Designer lines, counting the
*evaluated* `@(Compile)` item list rather than files on disk — correct, because the squash left
all 134 migrations on disk and removed them from compilation
(`ERP_RFQ_Automation.csproj:9-31`). Calibration by bisection is recorded at `Dockerfile:78-85`:
2,039,430 lines was the last proven pass, 2,225,279 the first measured failure.

Solving the growth series (baseline 27,579 lines; each new migration's Designer ≈ 27,579 + 174k):

| threshold | migrations of headroom | days at 6.7/day |
|---|---|---|
| 600,000 lines (proposed amber) | ~19 | **~2.9** |
| 1,200,000 lines (current red) | ~38 | **~5.7** |
| ~2,039,430 lines (last proven pass) | ~61 | ~9.1 |

The cadence figure is measured, not assumed: 134 migrations across 22 active days
(2026-07-15 → 2026-08-10); the most recent seven active days (Aug 4–10) carry
7+10+6+3+6+7+8 = **47 migrations, 6.7/day**. A line-count threshold with under six days of
headroom will always fire mid-sprint. **Line counts are a backstop, not a schedule.**

**Policy:**

1. **Trigger — time and gate, not lines.** Squash at **every gate closure**, and in any case
   never let more than **25 migrations** accumulate since the current baseline. Add a
   **600,000-line amber warning** to the Dockerfile guard (non-fatal, printed) as a backstop;
   keep red at 1,200,000. Do **not** raise the red threshold — it is 59% of the last proven-good
   point and raising it trades a readable build failure for an OOM four minutes into a deploy.
2. **Owner.** One named owner (the database owner, currently the CTO). Executed on a branch,
   never on a release day, never by whoever happens to hit the guard.
3. **Procedure.** Already written and reproducible:
   `MigrationsBaseline/regenerate-baseline-sql.py:4-27` — build DB_A by applying the whole
   pre-squash chain to a fresh PostgreSQL 16, `pg_dump --schema-only --no-owner`, regenerate,
   re-verify. Automate steps 1–4 into `scripts/db/verify-squash.sh` so the next squash is a
   command rather than a night.
4. **What "verified" must mean.** Tonight proved a byte-identical `pg_dump` across clusters is
   achievable, so **anything less than byte-identical is a policy failure, not a judgement
   call.** The bar, all of it, or the squash does not merge:
   - a. `pg_dump --schema-only --no-owner` of DB_A (full chain) and DB_B (baseline only), on
     fresh clusters, **byte-identical**. 40,073 lines tonight, across four clusters, one of
     them built by an independent certifier from this tree.
   - b. `Support/schema-parity-queries.sql` run against both with
     `psql -X -q -A -F '|' -t -v ON_ERROR_STOP=1`, **empty diff in both directions**. 7,558
     rows tonight.
   - c. The things a dump hides, asserted separately: **NULL-vs-explicit ACLs** (a naive
     comparison `COALESCE`s them away, and doing so nearly erased 6 load-bearing `REVOKE`s),
     stored parse trees (962 objects), and column ordinals **including dropped-column
     tombstones**.
   - d. Rollup invariants pinned as absolute numbers in the PR body: `rls_enabled`,
     `rls_forced`, `policies`, `tgenabled='A'`, `prosecdef`, column ACLs, `EXCLUDE`, `CHECK`.
     Any change is a finding, not a rounding difference.
   - e. Full suite green **and** the Down/Up guard green
     (`SquashedBaselineMigrationPostgreSqlTests.cs`) — the only thing keeping `90_down.sql`
     (384 lines, 365 `DROP`s) in step with the twelve Up scripts.
   - f. **Every migration-identity assertion the squash breaks is rewritten to assert the
     property, never deleted**, and a mechanical diff sweep proves no assertion was removed
     without disclosure. Tonight that sweep caught six silently removed assertions, including
     an entire tenant-role RLS block (`SET LOCAL ROLE nexora_tenant_app`, cross-tenant `INSERT`
     expecting 42501) that nothing else in the suite covered.
5. **Do not delete the superseded migrations.** They cost nothing (already out of
   `@(Compile)`) and they are the only record of *why* each object exists; the baseline records
   only *what*. Keep them with a `Migrations/README.md` explaining their status.

### 3. The parity gate — stopping model/database divergence

This is the load-bearing change, and it is what makes every other option in §1 safe.

**Gate SP-1 — `SchemaParityPostgreSqlTests`, one new test class in the PostgreSQL collection,
no new tooling:**

1. Container A: `Database.MigrateAsync()` — the real deployment path.
2. Container B: `Database.EnsureCreatedAsync()` — the model's own opinion, and *exactly what
   the SQLite lane trusts*. (`EntitlementClaimPostgreSqlTests.cs:46` already does
   `EnsureCreatedAsync()` against a PostgreSQL container, so the pattern is proven here.)
3. Compare the two catalogues over the **EF-expressible subset only**: tables; columns (name,
   store type, nullability, default); primary keys; unique constraints; indexes (column list,
   uniqueness, predicate); check constraints; foreign keys. Compare by **shape, not by name** —
   the 108-vs-23 gap in the Context section is what happens when you compare by name.
4. **Fail on B∖A** — anything the model declares that the database does not have. This is the
   defect class that ships broken code: `EnsureCreated()` gives the SQLite lane a table that
   production does not have, all 1,937 SQLite tests stay green, and the query raises `42P01` in
   production.
5. **Report A∖B at warning level** with a committed allow-list. A∖B is expected and large: it
   is the 111 CHECK constraints, 37 FKs, 18 indexes and 2 tables that live only in raw SQL.
   The allow-list makes them *declared* rather than *unexplained*, and a new entry appearing
   without a corresponding allow-list line is a review prompt.

Objects EF cannot model — policies, triggers, functions, grants — are out of scope for SP-1 by
construction; they are covered by SP-2.

**Gate SP-2 — wire `schema-parity-queries.sql` into CI.** Today that file is referenced from
exactly three code comments and nothing executes it: it is a manual artefact that proved parity
*once*. Make it a build step against a migrated container, with its 17-section output diffed
against a committed fixture. Any dropped policy, revoked grant, downgraded `ENABLE ALWAYS`
trigger or lost column ACL then fails a build instead of a customer.

**Three supporting changes:**

- **Stop suppressing `PendingModelChangesWarning` silently.** It is ignored in three places —
  `CoreSalesPostgreSqlTests.cs:31`, `SquashedBaselineMigrationPostgreSqlTests.cs:59`,
  `HttpIntegration/Release01BHttpApplication.cs:94`. Each is a place where the runtime is told
  not to complain. Each needs a one-line comment saying why, or removal.
- **Extend CI triggers.** `.github/workflows/ci.yml:3-7` fires only on `main` and
  `reliability-hardening`. The active branch (`wip/phase1-base-journey-20260806`) gets no push
  CI at all — only a PR into `main` would run it. Add the working branches.
- **Drop the unused SQL Server provider** (`ERP_RFQ_Automation.csproj:54`). `UseSqlServer`
  appears zero times; it is compile-time weight on a build that just OOM'd.

### 4. Expansion — what changes the day there is a real tenant

Nothing in the current lifecycle survives contact with data. Six specific changes:

1. **Drop-and-recreate stops being available.** `stamp-existing-database.sql:13-20` currently
   *recommends* dropping and recreating, correctly, because Nexora is pre-launch. Post-launch
   the only path is stamp + verified parity, and the script's own warning at `:26-31` becomes
   load-bearing: *"It does not verify that the database's SCHEMA matches the baseline. If the
   database drifted, stamping it hides that drift instead of fixing it."* The parity diff
   becomes a production procedure, not a convenience.
2. **`lock_timeout = 0` must go.** `MigrationsBaseline/Sql/01_schema_and_extensions.sql:17-18`
   sets `statement_timeout = 0` and `lock_timeout = 0` — "wait forever". Against live traffic
   that is the classic outage: an `ALTER TABLE` queues behind one long-running read, and
   because PostgreSQL's lock queue is FIFO, **every subsequent statement on that table blocks
   behind the ALTER**. Production migrations must set a small `lock_timeout` (3 s) and retry.
   One line; very large blast radius.
3. **`AlterColumn` on a policy column raises `0A000`, and the schema has already been shaped
   around it.** The structural evidence is stronger than any comment: across 43,769 lines and
   134 migrations there is **not one raw `ALTER COLUMN … TYPE`**, and only **39 `AlterColumn`
   calls** against **439 `AddColumn`** and **439 `DropColumn`**. Column *type* evolution has
   effectively been designed out of this schema — while 220 tables carry
   `nexora_tenant_isolation`. The workaround already exists in one place:
   `Migrations/20260725022734_Release01BIntakeIdentityAcceptance.cs` runs preflight guard
   (`:42`) → backfill (`:163`) → `AlterColumn` (`:113`, `:237`) → `DROP POLICY` (`:442`) →
   re-`CREATE POLICY` (`:443-445`), five hand-authored parts that `dotnet ef migrations add`
   could never produce.
   *Policy:* a helper that drops and recreates the policy **from the catalogue definition**
   (`pg_get_expr(polqual, polrelid)`) rather than from a transcribed literal, so the recreated
   predicate is provably the one that was there. Transcription is how a policy comes back
   subtly wider than it was.
   Note also `Dockerfile:56-61`: the `0A000` path is *already live* on any route that loses
   `TargetModel` — with an empty model the generator's `type != oldType` test is always true and
   `AlterColumn` emits a bare `ALTER COLUMN … TYPE` even when nothing changed. That is why the
   Designer files cannot simply be deleted.
4. **Expand/contract becomes mandatory** for every nullability and type change: add nullable →
   backfill in bounded batches → `ADD CONSTRAINT … CHECK (col IS NOT NULL) NOT VALID` →
   `VALIDATE CONSTRAINT` → flip → drop old. The idiom already exists — the schema carries **3
   `NOT VALID` foreign keys**.
5. **Online index builds become the default.** Exactly **1 of 134** migrations used
   `CREATE INDEX CONCURRENTLY` with `suppressTransaction: true`
   (`Migrations/20260722043733_CreateCustomFieldIdempotencyIndex.cs:14,24`). Post-launch every
   index on a populated table needs it — which means those migrations cannot be transactional,
   so they must be idempotent (`IF NOT EXISTS`, as that one already is) **and** must handle the
   `INVALID` index a failed concurrent build leaves behind. Add that cleanup to the pattern.
6. **Move migrations out of container start.** `Database__ApplyMigrationsOnStartup=true` is
   right today: `Program.cs:820-822` explains it guarantees RLS/policy installation is atomic
   with the rollout. With data, a slow DDL blocks the boot, the health check fails, and the
   platform kills the instance mid-migration. `Program.cs:821` already supports
   `Database:ApplyMigrationsOnStartup=false`; flip it and add a pre-deploy release step. Under
   R2 that step is also where the client's DBA reviews the SQL before it runs — which only
   works because the DDL is in `.sql` files.

Add to this: a tested restore before any production migration. `NEXORA_PHASE1_DECISION_REGISTER.md`
E42 already records that no backup exists for the document estate; the database needs the same
discipline named explicitly.

### 5. The SQLite lane — keep it, and stop calling it an integration lane

**What it can prove.** Relational translation of the global query filters, FK enforcement,
unique-index enforcement, LINQ→SQL shape, and all service/repository logic. `TestDb.cs:8-18`
describes this accurately and the choice of SQLite over EF InMemory is correct. It is fast and
parallel, and it carries 1,937 of 3,481 test methods.

**What it cannot prove — and this must be written down, not assumed.** No RLS, no policies, no
roles, no `GRANT`/`REVOKE`, no `SECURITY DEFINER`, no triggers, no `EXCLUDE`, no partial-index
predicates, no `citext`, no `NOT VALID`, no `information_schema` sweep. Beyond that:

- It builds its schema with `EnsureCreated()` from the **model**, so it is *structurally
  incapable* of catching model/database divergence — it is the mechanism that let a whole
  gate's schema go green while dead on PostgreSQL.
- Production code has **136 `IsNpgsql()` branches and 0 `IsSqlite()`**, so the lane always
  executes the *else* arm of 136 production paths.
- **19 sites disable CHECK constraints** with `PRAGMA ignore_check_constraints` to make SQLite
  accept rows PostgreSQL rejects. Every one is a row shape production would refuse.
- Decimals map to TEXT; `DateTimeOffset` ordering does not translate (`BillingRevenueIntegrityTests.cs:1451-1490`
  carries an entire `DbContext` subclass to work around it); `varchar(n)` lengths are ignored
  (`PlatformBillingTests.cs:1590`: "SQLite, which ignores varchar lengths, stayed green").

**Should it continue? Yes — with three conditions.**

1. **Rename what it is.** It is a *portable unit lane*, not an integration lane. The
   `Category=PostgreSQL` trait already splits it cleanly (388 methods, cross-checked two ways
   with zero drift). Say in `TESTING.md` that a test asserting a *database* behaviour —
   isolation, immutability, privilege, constraint — belongs in the PostgreSQL lane by
   definition.
2. **A floor, not a ceiling.** Any entity carrying a query filter must have at least one
   PostgreSQL-lane test exercising it under `SET LOCAL ROLE nexora_tenant_app`. Today 69 test
   methods assert real RLS enforcement and all are in the right lane — that is a good position
   to hold with a rule rather than by habit.
3. **SP-1 must exist**, because `EnsureCreated()` is precisely what makes divergence
   invisible.

**Rejected: move everything to Testcontainers.** All three PostgreSQL collections are
`DisableParallelization = true` (`PostgreSqlTestDatabase.cs:9`). Serialising 3,481 methods on
one container would take the suite from minutes to impractical, and the team would stop running
it — which is a worse outcome than an honest, limited fast lane. Fix the honesty problem with
the gate, not by deleting the lane.

---

## Migration path and honest cost

| Phase | When | Work | Cost |
|---|---|---|---|
| **0** | This week, before more migrations land | `scripts/db/verify-squash.sh` automating the four-cluster dump + parity diff; wire `schema-parity-queries.sql` into CI with a committed fixture (SP-2); make the twelve `.sql` files standalone (own GUCs; move the `__EFMigrationsHistory` revokes) | **1 day** |
| **1** | Gate 3 boundary | SP-1 parity gate; then resolve the 23 constraints + 9 indexes one at a time — for each, either author the migration or delete the model declaration | **2 days** (≈1.5 of it curating the initial allow-list) |
| **2** | Standing | Squash at every gate closure, hard ceiling 25 migrations; amber warning at 600,000 Designer lines; `Migrations/README.md` | **~2 h per squash** |
| **3** | Before the first real tenant | `lock_timeout`/`statement_timeout` policy; flip `ApplyMigrationsOnStartup` to false + pre-deploy release step; expand/contract playbook; catalogue-driven policy drop/recreate helper; retire the drop-and-recreate recommendation in `stamp-existing-database.sql` | **3 days** |

**Total ≈ 6 engineer-days across three gates, plus ~2 hours per squash.**

**What this does not buy, stated plainly.** It does not reduce the effort of authoring
migrations. It does not make the schema smaller or simpler — 232 policies and 142 functions are
the cost of database-enforced tenant isolation, and ADR-0005 chose that deliberately. It does
not remove the squash; under any EF-based option the squash is permanent maintenance. Phase 1
will also surface real work: some of the 23+9 divergences will turn out to be indexes that
production genuinely lacks, and fixing those is not free.

**What it buys.** The next OOM, the next dropped policy, and the next invisible model
divergence all fail in CI instead of in a deploy or in front of a customer. And the decision
stays reversible: the `.sql` tree is the portable artefact, so moving to DbUp later is a change
of runner, not a rewrite.

---

## What I would NOT do, and why

1. **Would not adopt Flyway or sqitch.** A JVM or a Perl runtime added to an on-premise KSA
   appliance (R2), operated by the client's staff, for zero improvement on the divergence
   problem — which is the actual defect class.
2. **Would not go declarative (Atlas / migra).** A desired-state file cannot express the
   `information_schema` `DO` loop that creates 138 of the 232 policies; adopting one silently
   changes the rule that new tenant tables inherit isolation. And auto-generated DDL against
   232 policies, 36 `SECURITY DEFINER` functions and 49 column grants is a security change
   nobody reviewed.
3. **Would not raise the 1,200,000-line budget.** It was calibrated by bisection
   (`Dockerfile:78-85`) at 59% of the last proven-good point. Raising it trades a readable
   build failure for an OOM kill four minutes into a deploy.
4. **Would not delete the Designer files, ever.** `Dockerfile:47-61` proves both consequences:
   `[Migration]` and `[DbContext]` exist only in the Designer half, so a migration without one
   is not broken but *invisible* to `MigrationsAssembly` — `MigrateAsync()` would silently
   apply nothing. And without `BuildTargetModel` the target model is empty, so `AlterColumn`
   emits a bare `ALTER COLUMN … TYPE` and PostgreSQL raises `0A000` on any policy-covered
   column even when the type is identical.
5. **Would not delete the 134 superseded migrations.** They are already out of `@(Compile)` and
   cost nothing. They are the only record of *why*; the baseline records only *what*.
6. **Would not hand-transcribe raw SQL into a regenerated EF baseline.** 1,849 statements where
   one omission silently removes tenant isolation and every functional test still passes. The
   `pg_dump` replay exists precisely so parity holds by construction.
7. **Would not adopt `pgroll` now.** It is the right shape for zero-downtime expand/contract
   later, but it wants its own runtime and to own the schema's views. Revisit at Phase 3.
8. **Would not delete the SQLite lane.** 1,937 methods on a serialised single-container
   PostgreSQL suite would stop being run.

---

## Consequences

- **Positive.** Divergence becomes a build failure. The squash becomes a scheduled, scripted
  two-hour ritual with a byte-identical bar instead of an unplanned night. The security-bearing
  DDL is reviewed as SQL in diffable files rather than as C# string literals — which is also
  what makes the on-premise install runbook (R2) possible. The tool decision stays reversible.
- **Negative.** Two lanes still exist and their limits must be actively policed. The A∖B
  allow-list is a file somebody has to curate. Migration authoring gets slightly more
  ceremonious: model-shaped DDL in C#, everything else in a numbered `.sql` file.
- **Risk accepted.** SP-1 compares only the EF-expressible subset. Objects EF cannot model are
  covered by SP-2's catalogue diff, which is a *fixture* comparison — it detects change, not
  wrongness. Neither gate can tell you a policy predicate is too wide; only review can.
- **Open question for the product owner.** Materialising all 232 policies explicitly, and
  retiring the `information_schema` `DO` loop, would make the schema far easier for a human
  team to understand — the loop's own `Down()` already carries a rotting 16-table exclusion
  allowlist. It is a behavioural change to tenant isolation and needs its own decision. It is
  **not** proposed here.

---

## Reproduction

Everything asserted above can be re-run. The commands used for this ADR:

```bash
# Build memory (cgroup2 memory.peak; --scenario baseline|squashed)
scripts/dev/measure-build-memory.sh --snapshot --mode docker --memory 7g --scenario baseline

# Replay the baseline into a throwaway cluster. NOTE the two preconditions:
# __EFMigrationsHistory must exist (10_explicit_revokes.sql revokes on it), and all twelve
# files must run in ONE transaction (01 sets SET LOCAL check_function_bodies = false, which
# 02 depends on). Applied file-by-file, 5 of 12 fail.
docker run -d --name pg -e POSTGRES_PASSWORD=p -e POSTGRES_DB=nexora -e POSTGRES_USER=nexora \
  -p 55440:5432 postgres:16-alpine
{ echo 'CREATE TABLE public."__EFMigrationsHistory" ("MigrationId" varchar(150) NOT NULL,
        "ProductVersion" varchar(32) NOT NULL,
        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"));'
  cat Backend/ERP_RFQ_Automation/MigrationsBaseline/Sql/{00,01,02,03,04,05,06,07,08,09,10,11}_*.sql
} | psql "$CONN" -X -q -v ON_ERROR_STOP=1 --single-transaction -f -   # 3 s

# Catalogue fingerprint (expect: 232 / 110 / 232 / 300 / 32 / 36 / 2 / 379)
psql "$CONN" -X -A -t -c "select
   (select count(*) from pg_class c join pg_namespace n on n.oid=c.relnamespace
     where n.nspname in ('public','platform') and c.relrowsecurity)                as rls,
   (select count(*) from pg_class c join pg_namespace n on n.oid=c.relnamespace
     where n.nspname in ('public','platform') and c.relforcerowsecurity)           as forced,
   (select count(*) from pg_policy)                                                as policies,
   (select count(*) from pg_trigger where not tgisinternal)                        as triggers,
   (select count(*) from pg_trigger where not tgisinternal and tgenabled='A')      as enable_always,
   (select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace
     where n.nspname in ('public','platform') and p.prosecdef)                     as secdef,
   (select count(*) from pg_constraint where contype='x')                          as exclude_c,
   (select count(*) from pg_constraint c join pg_class t on t.oid=c.conrelid
      join pg_namespace n on n.oid=t.relnamespace
     where c.contype='c' and n.nspname in ('public','platform'))                   as check_c"

# Migration cadence (134 across 22 active days; 47 in the last 7)
ls Backend/ERP_RFQ_Automation/Migrations/*.cs | grep -v Designer \
  | sed 's/.*\/\([0-9]\{8\}\).*/\1/' | sort | uniq -c

# Raw-SQL share of the pre-baseline migrations
find Backend/ERP_RFQ_Automation/Migrations -name '*.cs' ! -name '*.Designer.cs' \
     ! -name '*ModelSnapshot.cs' -print0 \
  | xargs -0 grep -ohE 'migrationBuilder\.[A-Za-z]+' | sed 's/migrationBuilder\.//' \
  | sort | uniq -c | sort -rn

# Test-lane split (388 PostgreSQL / 1,937 SQLite / 1,156 pure unit = 3,481 methods)
cd Backend/ERP_RFQ_Automation.Tests
grep -rho '\[Fact\]' --include='*.cs' . | wc -l      # 3224
grep -rho '\[Theory\]' --include='*.cs' . | wc -l    # 257
grep -rho 'Trait("Category", "PostgreSQL")' --include='*.cs' . | wc -l
```
