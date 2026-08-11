# Tenant isolation in PostgreSQL — the model, the findings, and the gate

**Status:** design proposal. Nothing in this document has been applied. No schema, migration or
production code was changed to produce it.

**Evidence base:** every factual claim below was executed against the fully-migrated PostgreSQL 16
database in container `nexora-squash-a` (`nexora_a`, port 55444) on 2026-08-11. Behavioural proofs
ran either inside a transaction that was rolled back, or in a throwaway database that was dropped
afterwards; the container was verified unchanged at the end (232 policies, 0 rows in `"Users"`,
`nexora_identity_app.rolbypassrls = true`). Commands are reproduced verbatim so any claim can be
re-run.

---

## Part 1 — How to read a policy

This part assumes you have never used row-level security. If you have, skip to Part 2.

### 1.1 What RLS actually does

A normal `GRANT` answers *"may this role touch this table at all?"*. Row-level security answers
*"which rows?"*. They are two separate gates and **the grant gate runs first**. A table with a
perfect policy and no `GRANT` is not a tighter boundary — it is a table nobody can read, and
PostgreSQL raises `42501 permission denied` before it ever looks at a row.

Five things decide whether a row is visible. All five must be right:

| # | Thing | Where it lives | Failure mode if wrong |
|---|-------|----------------|-----------------------|
| 1 | The role holds the privilege | `GRANT ... TO nexora_tenant_app` | `42501` on every query — a 500, not a leak |
| 2 | The table has RLS turned on | `ALTER TABLE ... ENABLE ROW LEVEL SECURITY` | **Silent full-table read** |
| 3 | A policy exists that names the role | `CREATE POLICY ... TO nexora_tenant_app` | 0 rows, fail-closed |
| 4 | The policy expression is correct | `USING (...) WITH CHECK (...)` | **Silent leak, or silent no-op** |
| 5 | The role is actually bound by RLS | `FORCE`, `BYPASSRLS`, table ownership | **Silent full-table read** |

Rows 2, 4 and 5 fail *quietly*. That is the whole reason this document exists: no functional test
catches them, because from the application's point of view everything works — it just works for
more rows than it should.

### 1.2 The Nexora policy shape

Every tenant table carries exactly one policy, named `nexora_tenant_isolation`. This is the live
text on `public.customer_identifiers`, copied out of `pg_policies`:

```sql
POLICY "nexora_tenant_isolation"
  TO nexora_tenant_app
  USING      ("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id', true), ''))::bigint)
  WITH CHECK ("BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id', true), ''))::bigint)
```

Token by token:

- **`TO nexora_tenant_app`** — the policy only applies when the *current* role is
  `nexora_tenant_app`. `MultiTenancy/TenantRlsCommandInterceptor.cs` issues `SET LOCAL ROLE` before
  every command, so this is the role the request path runs as. A policy with no `TO` clause defaults
  to `PUBLIC` and applies to everyone, which is a different control — check for it.
- **`current_setting('nexora.business_unit_id', true)`** — reads a session variable. The second
  argument, `true`, means *missing_ok*: return `NULL` instead of raising if the variable was never
  set. Without it, every query on an unscoped connection would error instead of returning nothing.
- **`NULLIF(..., '')`** — `set_config` stores strings, and an unset variable can surface as the empty
  string rather than `NULL`. `NULLIF` normalises both to `NULL`. Without it, `''::bigint` raises
  `22P02 invalid input syntax`.
- **`::bigint`** — the comparison must be integer-to-integer. A text comparison would still work but
  would defeat the index.
- **The net effect when no tenant is set:** the right-hand side is `NULL`, so the predicate is `NULL`,
  which is not `true`, so **no row matches**. That is the fail-closed property the whole design rests
  on, and it is the reason `ResolveDatabaseRole` can safely fall through to the tenant role with no
  GUC. Proven live:

  ```
  BEGIN; SET LOCAL ROLE proof_tenant_app;
  SELECT count(*) FROM enable_only;        -> 0
  INSERT INTO enable_only VALUES (99,1,…); -> ERROR: new row violates row-level security policy
  ```

- **`USING` vs `WITH CHECK`** — `USING` filters rows you *read* (and the pre-image of rows you update
  or delete). `WITH CHECK` validates rows you *write* (the post-image of an insert or update).
  A policy with `USING` and no `WITH CHECK` lets a tenant write a row it can then never see —
  including a row stamped with another tenant's id. Both must be present.

### 1.3 The three roles

```
nexora_tenant_app     NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS   <- bound by RLS
nexora_identity_app   NOLOGIN NOINHERIT NOSUPERUSER   BYPASSRLS   <- sees every tenant
nexora_pipeline_app   NOLOGIN NOINHERIT NOSUPERUSER   BYPASSRLS   <- sees every tenant
```

(Verified: `SELECT rolname, rolbypassrls, rolinherit, rolcanlogin FROM pg_roles WHERE rolname LIKE 'nexora%'`.)

The runtime login role is a *member* of all three and is `NOINHERIT`, so it holds none of their
privileges until an explicit `SET LOCAL ROLE`. `Program.cs::ValidateRuntimeDatabaseRoleAsync`
refuses to start if that topology is not exactly right.

**A role with `BYPASSRLS` is not filtered at all.** Policies are not evaluated for it. Any code path
that reaches `nexora_identity_app` or `nexora_pipeline_app` has *no* database-level tenant isolation
— only the EF Core global query filters, which any caller can turn off with `IgnoreQueryFilters()`.

### 1.4 The four traps

**Trap 1 — the tenant column has five spellings.** Live census
(`pg_attribute` over all base tables in `public`/`platform`):

| Spelling | Tables |
|---|---|
| `BusinessUnitId` | 167 |
| `BusinessUnitID` | 20 |
| `business_unit_id` | 11 |
| `BUID` | 7 |
| `Buid` | 1 |

These are quoted identifiers. PostgreSQL folds nothing. `"BUID"` and `"BusinessUnitId"` are two
different columns and a policy written against the wrong one on a table that happens to have both
compiles cleanly and matches nothing — or matches everything, depending on which side it lands on.
**When you review a policy, read the column name against `\d <table>`, not against your memory.**

**Trap 2 — PERMISSIVE policies combine with OR, not AND.** Two permissive policies on one table
*widen* access. If you need to add a restriction, either edit the existing policy or use
`AS RESTRICTIVE`. Today every table has exactly one policy except none — 232 policies over 231
tables — so a second policy appearing on a tenant table is, by itself, a finding.

**Trap 3 — `USING` and `WITH CHECK` can legally disagree, and usually shouldn't.** Two live policies
disagree today (`ProductAttachments`, `SupplierPurchaseHistory`): the read side carries an extra
`IS NULL OR` branch the write side does not. See finding **F5**.

**Trap 4 — RLS does not bind the table owner unless you say `FORCE`.** This is the single most
important thing in this document and it is covered in Part 3, finding **F1**.

---

## Part 2 — The database as it stands tonight

```
tables (public + platform)                267
  RLS ENABLED                             232
  RLS FORCED                              110      <- see F1
  with >= 1 policy                        231      <- LedgerActorNonces has none, see F6
policies                                  232
  named nexora_tenant_isolation           220
  platform-fleet USING(true) -> pipeline   10
  other (2)                                 2
triggers (non-internal)                   300  (32 tgenabled='A')
functions (non-extension)                 142  (36 SECURITY DEFINER, 36/36 pin search_path)
column-level ACL entries                   49
pg_default_acl entries                      0      <- deny-by-default, see 2.1
```

Every number above matches the figures recorded in commit `06137ef`. Reproduce with:

```sql
SELECT 'rls_enabled', count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='r' AND c.relrowsecurity AND n.nspname NOT IN ('pg_catalog','information_schema')
UNION ALL SELECT 'rls_forced', count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='r' AND c.relforcerowsecurity AND n.nspname NOT IN ('pg_catalog','information_schema')
UNION ALL SELECT 'policies', count(*) FROM pg_policies;
```

### 2.1 Two things that are already right, and should be preserved

**Deny-by-default privileges.** `20260723120000_CompleteTenantRlsCoverage` ran
`ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ... FROM nexora_tenant_app` and then
`REVOKE ALL PRIVILEGES ON ALL TABLES`. `pg_default_acl` is now empty, which means a table created by
a future migration gets **no** grant to the tenant role. A new table is therefore unreachable rather
than unfiltered. This is the correct polarity and the CI gate in Part 5 depends on it.

**SECURITY DEFINER hygiene is clean.** All 36 `SECURITY DEFINER` functions pin `search_path`:

```sql
SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE p.prosecdef AND n.nspname IN ('public','platform')
  AND (p.proconfig IS NULL OR NOT EXISTS (SELECT 1 FROM unnest(p.proconfig) c WHERE c LIKE 'search_path=%'));
-- 0
```

The usual escalation via a shadowed unqualified reference is not available. Keep it that way.

### 2.2 The squash already fixed half of the "declared intent" problem

The brief describes 138 policies created by a `DO`-loop sweeping `information_schema`. That was true
of the *migration history*. It is no longer true of what a fresh database executes:
`MigrationsBaseline/Sql/08_row_level_security.sql` contains **232 literal `CREATE POLICY` statements**
and 232 `ALTER TABLE ... ENABLE ROW LEVEL SECURITY`, and
`MigrationsBaseline/Sql/03_tables_and_sequences.sql` contains **111 literal
`FORCE ROW LEVEL SECURITY`** statements. The sweep's output is frozen as text.

That is a real improvement and it should be said plainly. What it does **not** do:

- It froze the output, not the *intent*. `08_row_level_security.sql` says what is true; nothing says
  what *ought* to be true, so a wrong policy and a right policy are indistinguishable on review.
- It is a snapshot. The next table added by the next migration is governed by nothing.
- It froze the accidents too — the four dead `IS NULL OR` branches (F5) and the 122 missing `FORCE`
  declarations (F1) are now permanent unless deliberately corrected.

---

## Part 3 — Findings, ranked by real risk

### F1 — HIGH — `FORCE ROW LEVEL SECURITY` on 110 of 232 tables, producing two opposite silent failures on the owner connection

*Not in the brief. This is the largest finding.*

RLS does not apply to the table owner unless the table is declared `FORCE`. Every table in this
database is owned by one role, and `TenantRlsCommandInterceptor`'s own XML documentation states that
`ResolveDirectMigrationConnection` reuses the runtime username, so **the runtime login role is also
the table owner**. That makes the owner path a live production path, not a DBA curiosity:
`TenantPurgeExecutor`, `TenantDataReset`, `ModuleCatalogReconciler` and every migration run on it.

122 tables are `ENABLE`-only. They include `Users`, `Quotes`, `RFQ`, `Orders`, `Products`,
`Inventory`, `Suppliers`, `RolePermissions`, `SlaPolicies`, `Email_Configurations`,
`customer_identifiers`, `CommercialCases` and `platform.PlatformAuditLogs`.

```sql
SELECT n.nspname||'.'||c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE c.relkind='r' AND c.relrowsecurity AND NOT c.relforcerowsecurity
  AND n.nspname NOT IN ('pg_catalog','information_schema') ORDER BY 1;   -- 122 rows
```

**Proof, direction 1 — `ENABLE`-only leaks every tenant to the owner.** Built in a throwaway
database using the exact role topology `ValidateRuntimeDatabaseRoleAsync` enforces (owner `LOGIN
NOINHERIT NOSUPERUSER NOBYPASSRLS`, member of the tenant role):

```
############ AS OWNER (runtime login role, NO 'SET LOCAL ROLE' issued) ############
proof_owner|enable_only rows visible to OWNER:|2
proof_owner|forced_tbl  rows visible to OWNER:|0
LEAKED:TENANT-2 SECRET
```

**Proof, direction 2 — `FORCE` makes the owner see nothing, and the tenant purge silently
no-ops.** `TenantPurgeExecutor.cs:222-247` opens the owner connection, sets
`session_replication_role='replica'`, and issues `DELETE FROM <table> t WHERE <predicate>` with **no
`nexora.business_unit_id`**. On a `FORCE` table the owner is bound by RLS, and the only policy is
`TO nexora_tenant_app` — which does not apply, because PostgreSQL matches policy roles with
`has_privs_of_role()`, and the owner is `NOINHERIT`:

```
proof_owner is NOINHERIT member of proof_tenant_app: true / has_privs: false
BEGIN
DELETE 0        <- forced_tbl
DELETE 5        <- enable_only
COMMIT
rows LEFT in forced_tbl  after purge:|5
rows LEFT in enable_only after purge:|0
```

`DELETE 0` is not an error, and `TenantPurgeExecutor` only records a table when `rows > 0`. A tenant
offboarding would report success having deleted nothing from any `FORCE` table.

This is conditional on `ConnectionStrings:MigrationConnection` resolving to a non-superuser owner —
which is exactly what `ResolveDirectMigrationConnection` produces when the setting is absent. If it
resolves to a superuser, RLS is bypassed entirely and the purge works, but then the largest
privilege in the system is being handed to a request-path-adjacent component. Both branches need an
answer; neither is currently asserted anywhere.

**Why this outranks everything else:** the `FORCE`/`ENABLE` split is not a graded control. It is a
coin flip between "the owner sees every tenant" and "the owner sees nothing and says it succeeded".
Neither is the intended behaviour, and the current 110/122 split means both are live simultaneously
on different halves of the schema.

**Remedy (Part 4, R2):** `FORCE` on every tenant-owned table, plus one declared, named policy for
the maintenance path — so the purge is authorised explicitly rather than by an ownership loophole.

---

### F2 — HIGH — `ResolveDatabaseRole` reaches a `BYPASSRLS` role on two paths driven by request-controlled input

*Adjudicates brief item 4.*

The brief's description is accurate but incomplete. The full decision table
(`TenantRlsCommandInterceptor.cs:235-306`):

| # | Condition | Role | `BYPASSRLS`? |
|---|-----------|------|--------------|
| A | `httpContextAccessor is null` && no tenant | `null` — **no `SET ROLE` at all** | owner, see F1 |
| B | `HttpContext is null` && no tenant | `nexora_pipeline_app` | **yes** |
| C | path `/api/platform/auth/login` | `nexora_pipeline_app` | **yes** |
| D | path `/api/Auth/Login` | `nexora_identity_app` | **yes** |
| E | path `/api/tenant-activation` | `nexora_identity_app` | **yes** |
| F | tenant present | `nexora_tenant_app` | no |
| G | path `/api/platform` | `nexora_pipeline_app` | **yes** |
| H | otherwise | `nexora_tenant_app`, no GUC | no — fail-closed |

**The guard the brief describes is real and correct.** Branch F sits above branch G deliberately, so
an impersonation token — a *tenant* token carrying `businessUnitId` — cannot downgrade a
`/api/platform` request onto the bypass role. The inline comment says so and it is right.

**Two paths still reach `BYPASSRLS` with attacker-influenced input.**

**B is the sharper one.** When `HttpContext` is null and there is no tenant, the code returns
`PipelineRole` — `BYPASSRLS`. Four lines below, branch H returns the tenant role for the *equivalent*
null-tenant case, and the XML documentation explains at length why:

> *"Deliberately NOT PipelineRole: that role is created BYPASSRLS … and the same verification showed
> it returning every tenant's rows both with and without FORCE. Routing the null case there would
> relabel the hole, not close it."*

Branch B routes the null case to exactly that role. It fires whenever the scoped interceptor is
resolved outside a live request: hosted services, fire-and-forget continuations, and any EF
enumeration that outlives the response. Whatever those callers do, they do it unfiltered.

**G is the broader one.** Any `/api/platform/*` request with no tenant claim runs `BYPASSRLS` — and
"no tenant claim" includes "no token at all". `/api/platform/auth/mfa/challenge`
(`PlatformAuthController.cs:115-118`) is `[AllowAnonymous]` and takes a fully attacker-controlled
body; it does not match `StartsWithSegments("/api/platform/auth/login")`, so it lands on branch G and
gets the same bypass. The authorization `FallbackPolicy` (`Program.cs:475-478`) stops unauthenticated
traffic reaching the other platform controllers — but that is the point: **for the entire
`/api/platform` surface, the database provides zero defence in depth.** RLS exists precisely so that
an authorization bug is not a cross-tenant breach, and on this surface it is switched off.

**Remedy:** branch B should return `TenantRole` (matching H's documented reasoning, and fail-closed
by F's own proof). Branch G should be narrowed to the specific anonymous platform endpoints that
demonstrably need it, with every other platform path running on a role that is bound by policy —
see Part 6.

---

### F3 — MEDIUM-HIGH — 78 foreign keys between tenant-owned tables omit the tenant column

*Adjudicates brief item 5 — and refutes "a single weaker FK".*

`FK_customer_identifiers_Customers_BusinessUnitId_CustomerId` does not exist in the database. What
exists is:

```sql
FK_customer_identifiers_Customers_CustomerId
  FOREIGN KEY ("CustomerId") REFERENCES "Customers"("ID") ON DELETE RESTRICT
```

Single-column, as reported. But it is one of **78**, not one:

```sql
WITH tenantcol AS (
  SELECT c.oid, a.attnum FROM pg_class c
  JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
  WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema')
    AND a.attname IN ('BUID','Buid','BusinessUnitID','BusinessUnitId','business_unit_id')
    AND a.attnum>0 AND NOT a.attisdropped)
SELECT (ch.attnum = ANY(k.conkey)) AS fk_includes_tenant_col, count(*)
FROM pg_constraint k JOIN tenantcol ch ON ch.oid=k.conrelid JOIN tenantcol pa ON pa.oid=k.confrelid
WHERE k.contype='f' GROUP BY 1;
-- f | 78
-- t | 322
```

322 of 400 tenant-to-tenant FKs already carry the tenant column — the composite `AK_*_BusinessUnitId_Id`
alternate keys exist for exactly this purpose. The 78 that do not include `Quotes -> Customers`,
`Orders -> Customers`, `RFQ -> Customers`, `Leads -> CommercialCases`, `Products -> Suppliers` and
`CustomerPurchaseOrderLines -> Products`.

**Proof that this is exploitable, not theoretical** (rolled back):

```
INSERT INTO customer_identifiers ("BusinessUnitId","CustomerId",…) VALUES (9001, 9002, …);
INSERT 0 1
FK ACCEPTED cross-tenant row: identifier BU=9001 -> Customer 9002 whose BUID=9002
```

RLS does not catch this. The identifier row is stamped `BusinessUnitId = 9001`, so it is legitimately
inside tenant 9001's boundary; the *pointer inside it* crosses. The blast radius depends on the
consumer: `CustomerResolution/LeadCustomerResolutionService.cs:283` and
`CustomerResolution/CustomerAliasLearner.cs:98,207` read `CustomerIdentifier` with
`IgnoreQueryFilters()`, which removes the ORM's filter as well. Under the tenant role RLS still
holds; on any path that reaches a `BYPASSRLS` role (F2), it does not.

Some of the 78 are benign — `Currency`, `Setup_Master`, `setUOM`, `SetCity/SetState/SetCountry` are
reference tables. That distinction is exactly what the manifest in Part 4 is for: today nothing
records which of the 78 are intentional.

---

### F4 — MEDIUM — the tenant-plane role can read the platform SMTP password and SendGrid key ciphertext

*Adjudicates brief item 2.*

Confirmed, and worse than "grants SELECT": the table has **no RLS at all**.

```
platform.PlatformEmailSettings   relrowsecurity=f   relforcerowsecurity=f   policies=0
platform.PlatformEmailSettings.SmtpPassword    {nexora_tenant_app=r/postgres, nexora_identity_app=r/postgres}
platform.PlatformEmailSettings.SendGridApiKey  {nexora_tenant_app=r/postgres, nexora_identity_app=r/postgres}
```

Live, as `nexora_tenant_app`:

```
SELECT * FROM platform."PlatformEmailSettings";                       -> ERROR: permission denied
SELECT "SmtpPassword","SendGridApiKey" FROM platform."PlatformEmailSettings";  -> succeeds
```

The column-level grant is the whole grant; there is no table-level `SELECT`, which is why `SELECT *`
fails and the targeted read succeeds. That is a deliberate, well-built column ACL — the 49 entries in
this database are careful work. The question is whether these two columns belong in it.

**Verdict: no.** The AES-256-GCM envelope is a genuine control and I am not calling this a breach.
But three things are true at once:

1. There is no row filter, so this is not "a tenant's own secret" — it is the *platform operator's*
   outbound mail credential, one row, shared by the fleet.
2. Encryption whose key lives in application config protects against a database-file or backup
   compromise. It does not protect against an application-level read, and an application-level read
   is precisely what this grant authorises. A tenant-plane SQL injection or an over-broad EF
   projection turns into ciphertext exfiltration, and the attacker then needs only the config key —
   which the same process holds.
3. The tenant plane has no legitimate need for it. Outbound platform mail is sent by the platform
   plane. `Email/PlatformEmailConnectionController.cs` is routed `api/platform/notifications/email`,
   which resolves to `nexora_pipeline_app`.

**Remedy:** revoke both columns from `nexora_tenant_app` and `nexora_identity_app`; leave the other
19 non-secret columns (`SmtpHost`, `FromAddress`, `OutboundGuard*`, …) if the tenant plane reads them
for display. Add `platform.PlatformEmailSettings` to the operator-only class in the manifest, and add
a CI assertion that no column named `%Password%`, `%ApiKey%`, `%Secret%` or `%Token%` in either schema
is readable by `nexora_tenant_app`.

---

### F5 — LOW-MEDIUM — four unreachable `IS NULL OR` branches in live policies

*Adjudicates brief item 3 — four, not five.*

`pg_policies` contains five policies matching `IS NULL`. One of them,
`nexora_ai_default_provisioning` on `public.AiProcessingPolicies`, tests
`"AllowedProvider" IS NULL AND "AllowedModel" IS NULL` — a legitimate value check on a non-tenant
column, not dead code. The other four are tenant-column branches:

| Table | Branch | In `USING` | In `WITH CHECK` |
|---|---|---|---|
| `public.Products` | `"BUID" IS NULL OR` | yes | no |
| `public.Inventory` | `"Buid" IS NULL OR` | yes | no |
| `public.ProductAttachments` | `product."BUID" IS NULL OR` | yes | no |
| `public.SupplierPurchaseHistory` | `product."BUID" IS NULL OR`, `supplier."BUID" IS NULL OR` | yes | yes |

Verbatim, from `pg_policies`:

```sql
-- public."Products"
USING      (("BUID" IS NULL) OR ("BUID" = (NULLIF(current_setting('nexora.business_unit_id', true), ''))::bigint))
WITH CHECK  ("BUID" = (NULLIF(current_setting('nexora.business_unit_id', true), ''))::bigint)
```

They are dead. `Products."BUID"`, `Inventory."Buid"` and `Suppliers."BUID"` are all `attnotnull = t`:

```sql
SELECT c.relname||'.'||a.attname, a.attnotnull FROM pg_class c JOIN pg_attribute a ON a.attrelid=c.oid
WHERE c.relname IN ('Products','Inventory','Suppliers') AND a.attname IN ('BUID','Buid') AND c.relkind='r';
-- Products.BUID  | t
-- Suppliers.BUID | t
-- Inventory.Buid | t
```

`UPDATE "Products" SET "BUID" = NULL` returns `UPDATE 0` on an empty table and would raise
`23502 not-null violation` on a populated one. The branch cannot be entered.

**Why it still matters at LOW-MEDIUM rather than "cosmetic":** these came from
`CompleteTenantRlsCoverage`'s `nullable_access` variable, which read `is_nullable` at the instant the
sweep ran. The branch encodes "master data with no tenant is shared to every tenant" — a real and
once-intended semantic. Today the intent is gone but the code is still there. If a future migration
relaxes any of those three columns to nullable, four tables become globally readable with no
migration touching a policy, no diff in `08_row_level_security.sql`, and no test failing. The dead
code is a loaded gun with the safety on.

**Remedy:** delete the four branches and add a CI assertion (Part 5, G6) that no
`nexora_tenant_isolation` policy's `USING` expression contains `IS NULL` against a tenant column.
That converts "currently unreachable" into "cannot be made reachable".

---

### F6 — LOW / NO ACTION — `public."LedgerActorNonces"`: RLS enabled, zero policies

*Adjudicates brief item 1: intended, already documented, already asserted — but inert.*

It is deliberate. `PostgreSqlProductionDialectTests.cs` carries a named exclusion with a written
rationale:

> *"Deliberately denied to the tenant role, not accidentally ungranted. This table has no EF entity
> and no query filter; it is written and read only by a privileged role, and CompleteLedgerKernelControls
> explicitly REVOKEs it from nexora_tenant_app."*

Confirmed. Grants are `nexora_pipeline_app` only:

```
nexora_pipeline_app | SELECT/INSERT/UPDATE/DELETE
postgres            | (owner)
```

Live, as `nexora_tenant_app`: `ERROR: permission denied for table LedgerActorNonces` — the *grant*
gate refuses, not the policy gate. So the deny-all RLS is **inert**: it denies nothing that the
missing grant did not already deny, and it is bypassed by the only role that can reach the table.

Keep it, because it is the right shape for a future in which the grant is loosened. But tighten the
guard: the current test excludes the table by name from the "RLS but no grant" assertion. Replace
that name-list exclusion with a positive class in the manifest — `OperatorOnly` — asserted as
*"RLS enabled, FORCE, zero policies naming `nexora_tenant_app`, and zero privileges held by
`nexora_tenant_app`"*. Then adding a grant later fails CI instead of silently converting the table
into a runtime `42501`.

---

### F7 — INFORMATIONAL — two nullable tenant columns whose policies are equality-only

`public."Users"."BUID"` and `public."ProductSubCategories"."BusinessUnitID"` are the only nullable
tenant columns left in either schema:

```sql
SELECT n.nspname||'.'||c.relname||'.'||a.attname FROM pg_class c
JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid
WHERE c.relkind='r' AND n.nspname NOT IN ('pg_catalog','information_schema')
  AND a.attname IN ('BUID','Buid','BusinessUnitID','BusinessUnitId','business_unit_id')
  AND a.attnum>0 AND NOT a.attisdropped AND NOT a.attnotnull;
-- public.ProductSubCategories.BusinessUnitID
-- public.Users.BUID
```

Neither policy carries an `IS NULL OR` branch — `Users` reads
`USING ("BUID" = (NULLIF(current_setting(…), ''))::bigint)`. So a row with a `NULL` tenant is
invisible to the tenant plane *and* un-insertable by it: fail-closed, correct. But it is reachable
by the `BYPASSRLS` identity plane, which holds column-level `UPDATE` on
`Users.Password_Hash`, `IsActive`, `LastLogin`, `ModifiedBy`, `ModifiedOn`, `DeactivatedAtUtc`.
A `NULL`-tenant user is an orphan that only the bypass plane can create or see. Make both columns
`NOT NULL` and the ambiguity disappears.

---

### Ranking summary

| Rank | ID | Finding | Risk | Brief item |
|---|---|---|---|---|
| 1 | F1 | 122 tables `ENABLE`-only; owner leaks on those, purge silently no-ops on the other 110 | High | — (new) |
| 2 | F2 | Two `BYPASSRLS` paths in `ResolveDatabaseRole` reachable with request-controlled input | High | 4 |
| 3 | F3 | 78 tenant-to-tenant FKs omit the tenant column; cross-tenant reference proven accepted | Med-High | 5 |
| 4 | F4 | Tenant role holds column `SELECT` on platform SMTP/SendGrid ciphertext, no RLS on the table | Medium | 2 |
| 5 | F5 | Four unreachable `IS NULL OR` branches, re-armed by any future nullability change | Low-Med | 3 |
| 6 | F6 | `LedgerActorNonces` deny-all is intended but inert; guard is a name-list exclusion | Low | 1 |
| 7 | F7 | Two nullable tenant columns; fail-closed today, orphan-capable via the bypass plane | Info | — |

---

## Part 4 — How isolation should be declared

The current state describes itself. It does not declare itself. A reviewer looking at
`08_row_level_security.sql` can see what the policy *is* and has no way to know what it *should be*.
Three changes fix that, in order of value.

### R1 — A manifest: one line per table, stating the intent

A single checked-in file is the source of truth for what every table's isolation is *supposed* to be.
It must live outside the database so that a wrong database is a diff rather than a definition.

`docs/database/tenant-isolation-manifest.csv`:

```csv
schema,table,class,tenant_column,parent_table,parent_fk,notes
public,customer_identifiers,TenantOwned,BusinessUnitId,,,
public,Users,TenantOwned,BUID,,,identity plane holds column-scoped UPDATE
public,QuoteItems,DerivedFromParent,,Quotes,QuoteID,line table; parent carries BusinessUnitID
public,Currency,GlobalReference,,,,shared FX reference; readable by all tenants
public,LedgerActorNonces,OperatorOnly,BusinessUnitId,,,pipeline-only; REVOKEd from tenant role
platform,UsageEvents,PlatformFleet,,,,fleet metering; pipeline plane only
platform,PlatformEmailSettings,OperatorOnly,,,,F4: revoke SmtpPassword/SendGridApiKey
```

Five classes, and every table in `public` and `platform` must carry exactly one:

| Class | Required declaration | Required proof |
|---|---|---|
| **TenantOwned** | RLS + FORCE + exactly one policy `nexora_tenant_isolation TO nexora_tenant_app`, `USING` and `WITH CHECK` both exactly `"<tenant_column>" = (NULLIF(current_setting('nexora.business_unit_id', true), ''))::bigint`, column `NOT NULL`, `SELECT/INSERT/UPDATE/DELETE` granted to `nexora_tenant_app` | cross-tenant read returns 0; cross-tenant write rejected |
| **DerivedFromParent** | RLS + FORCE + one policy whose expression is an `EXISTS` against the declared parent on the declared FK, no `IS NULL` disjunct | same, seeded through the parent |
| **GlobalReference** | no tenant column; RLS off; `SELECT` only to `nexora_tenant_app`; no `INSERT/UPDATE/DELETE` | tenant role cannot write |
| **OperatorOnly** | RLS + FORCE; zero policies naming `nexora_tenant_app`; zero privileges held by `nexora_tenant_app` (table **or** column level) | tenant role gets `42501` |
| **PlatformFleet** | RLS + FORCE + one policy `TO nexora_pipeline_app`; no tenant-plane privilege | tenant role gets `42501` |

The `TenantOwned` expression is stated *exactly*, not "contains `nexora.business_unit_id`". That
matters: the existing assertion in `PostgreSqlProductionDialectTests` uses
`position('nexora.business_unit_id' in pg_get_expr(...)) > 0`, which passes for every one of the four
dead-branch policies in F5 and would pass for a policy comparing the wrong column. Comparing
normalised text against a generated expected string is the difference between "a policy exists" and
"the right policy exists".

### R2 — `FORCE` everywhere, plus a named maintenance policy

Close F1 in both directions:

1. `ALTER TABLE ... FORCE ROW LEVEL SECURITY` on all 122 `ENABLE`-only tables. This removes the
   owner leak.
2. That alone would break the purge (proven above: `DELETE 0`). So the purge must stop relying on an
   ownership loophole and start being *authorised*. Two options, in preference order:
   - **A dedicated `nexora_maintenance` role** with `BYPASSRLS`, `NOLOGIN`, no grants except the
     purge targets, used only by `TenantPurgeExecutor` / `TenantDataReset` via `SET LOCAL ROLE`.
     `session_replication_role` still needs superuser, so this is a partial move — but it makes the
     purge's reach declared rather than incidental.
   - **A declared owner policy** on every tenant table:
     `CREATE POLICY nexora_maintenance_scope ON <t> TO <owner_role> USING ("<col>" = NULLIF(current_setting('nexora.maintenance_business_unit_id', true), '')::bigint)`,
     with the purge setting that GUC. The purge then deletes exactly one tenant *by policy*, and a
     purge that forgets to set the GUC deletes nothing — which is the correct failure.
3. Either way, `TenantPurgeExecutor` must treat "0 rows deleted from a table the manifest says the
   tenant owns" as an error rather than as an unrecorded success.

### R3 — Generate the policies from the manifest; assert the generation

Do not hand-write 232 policies again. The manifest is the input; the generator emits
`08_row_level_security.sql`; CI asserts that regenerating from the manifest reproduces the checked-in
file byte for byte, and that the file reproduces the live catalogue.

```
manifest.csv --generate--> 08_row_level_security.sql --apply--> pg_catalog
     ^                                                              |
     +----------------- CI asserts all three agree -----------------+
```

`MigrationsBaseline/regenerate-baseline-sql.py` already exists and already regenerates the baseline
from a live database. Extend it, rather than building a second mechanism: add a `--from-manifest`
mode for section 08 and a `--verify` mode that diffs manifest-derived expectations against the
catalogue. The parity harness at
`Backend/ERP_RFQ_Automation.Tests/Support/schema-parity-queries.sql` (720 lines, sections
`08a_rls` / `08a_inv_tenant_table_without_rls` / `08b_policy` / `08c_policy_count`) is the reader
half and needs no change — it already emits exactly the right shape. What is missing is the
*expected* side.

---

## Part 5 — The CI gate

Two jobs. The first is fast and runs on every PR. The second is the byte-parity diff that already
exists and runs on migration changes.

### Job 1 — `tenant-isolation-gate` (every PR, ~90s)

Apply all migrations to an empty PostgreSQL 16, then run nine assertions. Each fails with the exact
list of offending tables, not a boolean.

| ID | Assertion | Catches |
|---|---|---|
| **G1** | Every base table in `public`/`platform` appears in the manifest exactly once | A new table with no declared intent. **This is the gate that makes "you cannot add a table without one" true.** |
| **G2** | Every `TenantOwned`/`DerivedFromParent`/`OperatorOnly`/`PlatformFleet` row has `relrowsecurity AND relforcerowsecurity` | F1 |
| **G3** | Every `TenantOwned` table has exactly one policy, named `nexora_tenant_isolation`, `TO nexora_tenant_app`, `ALL`, `PERMISSIVE`, whose normalised `USING` **and** `WITH CHECK` equal the generated expected string for its declared column | F5, wrong-column policies, Trap 2 |
| **G4** | Every `TenantOwned` tenant column is `attnotnull` | F7 |
| **G5** | Grant polarity: `TenantOwned`/`DerivedFromParent` hold exactly `SELECT,INSERT,UPDATE,DELETE` for `nexora_tenant_app`; `GlobalReference` holds exactly `SELECT`; `OperatorOnly`/`PlatformFleet` hold nothing — including no *column-level* privilege | F4, F6, and the historical "policy but no grant" 500s |
| **G6** | No policy on a `TenantOwned` or `DerivedFromParent` table contains `IS NULL` against a tenant column | F5, permanently |
| **G7** | Every FK between two tables the manifest marks `TenantOwned` includes the tenant column on both sides, unless the pair appears in a checked-in `fk-exceptions.csv` with a written reason | F3 |
| **G8** | No column in either schema matching `%Password%\|%ApiKey%\|%Secret%\|%Token%\|%PrivateKey%` is readable by `nexora_tenant_app` at table or column level | F4 |
| **G9** | The set of `(role, rolbypassrls)` pairs equals the checked-in expected set; every `SECURITY DEFINER` function pins `search_path` | privilege drift, §2.1 regression |

G5 must use `has_any_column_privilege` as well as `has_table_privilege`. The existing assertions in
`PostgreSqlProductionDialectTests` use only the latter, which is exactly why F4's column-level grant
is invisible to CI today.

Reference implementation of G3, the assertion that does the most work:

```sql
WITH manifest(schema_name, table_name, tenant_column) AS (
    SELECT * FROM json_to_recordset(:manifest_json)
        AS x(schema_name text, table_name text, tenant_column text)
),
expected AS (
    SELECT m.*,
           format('(%I = (NULLIF(current_setting(''nexora.business_unit_id''::text, true), ''''::text))::bigint)',
                  m.tenant_column) AS expr
    FROM manifest m
),
actual AS (
    SELECT p.schemaname, p.tablename, count(*) AS n,
           min(p.policyname) AS name, min(p.cmd) AS cmd,
           min(array_to_string(p.roles, ',')) AS roles,
           min(regexp_replace(p.qual, '\s+', ' ', 'g')) AS using_expr,
           min(regexp_replace(p.with_check, '\s+', ' ', 'g')) AS check_expr
    FROM pg_policies p GROUP BY 1, 2
)
SELECT e.schema_name, e.table_name,
       coalesce(a.n, 0) AS policy_count, a.name, a.roles, a.using_expr, e.expr AS expected
FROM expected e LEFT JOIN actual a
  ON a.schemaname = e.schema_name AND a.tablename = e.table_name
WHERE a.n IS DISTINCT FROM 1
   OR a.name <> 'nexora_tenant_isolation'
   OR a.cmd  <> 'ALL'
   OR a.roles <> 'nexora_tenant_app'
   OR a.using_expr IS DISTINCT FROM e.expr
   OR a.check_expr IS DISTINCT FROM e.expr;
-- must return zero rows
```

### Job 2 — `schema-parity` (migration changes only)

Already designed and already written. `schema-parity-queries.sql` against the migration-built
database and against the squashed baseline, `diff -u`, fail on any line. Add one step: run it against
the *previous* commit's database too, and fail on any **removed** line in sections `08a` / `08b` /
`08c` / `05` / `06`. A control that disappears is the failure mode that matters; a control that
appears is a normal feature.

### What the gate deliberately does not do

It does not assert row counts or business behaviour. That is Part 6's job. Keeping the catalogue gate
free of data means it runs in seconds and can be required on every PR without argument.

---

## Part 6 — The negative-test standard

The precipitating incident: an entire cross-tenant-refusal test was deleted, nothing else in the
suite covered it, and it came back only because an automated diff sweep noticed. That is a coverage
*topology* problem, not a diligence problem. The fix is to stop letting coverage be the sum of
individually deletable files.

Today, isolation testing is in three disconnected places, none of which is the thing that was lost:

- `TenantIsolationTests.cs` (415 lines) — EF Core global query filters, on SQLite. **Not RLS.**
  SQLite has neither roles nor row-level security.
- `RolePermissionTenantIsolationTests.cs` (90 lines) — a controller with a mock repository. **No
  database.**
- ~20 `*PostgreSqlTests.cs` files issue `SET LOCAL ROLE nexora_tenant_app` incidentally, each proving
  isolation for its own feature as a side effect.

Nothing enumerates the catalogue and asserts refusal. Proposed standard: three tiers, and the
minimum proof for a tenant-owned table is **tiers 1 and 2 together**.

### Tier 1 — Catalogue (all 267 tables, no data, every PR)

Part 5's G1–G9. Proves the *declaration* is right. Cost: seconds. Cannot be deleted per-table,
because it iterates the manifest.

### Tier 2 — Generated behavioural refusal (every `TenantOwned` and `DerivedFromParent` table)

One data-driven xUnit `[Theory]` with `[MemberData]` sourced from the manifest. Not 232 hand-written
tests — 232 *cases* of one test. For each table, inside a transaction that is always rolled back:

1. Seed business units `A` and `B`.
2. Forge one minimal row per tenant. Column values come from the catalogue: satisfy every
   `attnotnull` column with no default using a type-driven default (`0` / `''` / `now()` /
   `gen_random_uuid()`), stamp the declared tenant column. Run the seed on the owner connection under
   `SET LOCAL session_replication_role = 'replica'` so FK order is irrelevant — the same mechanism
   `TenantPurgeExecutor` already uses.
3. `SET LOCAL ROLE nexora_tenant_app` + `set_config('nexora.business_unit_id', A)` and assert **four**
   refusals:

   | Assertion | Expected |
   |---|---|
   | `SELECT count(*)` | exactly 1 — A's row, never B's |
   | `SELECT ... WHERE <pk> = <B's pk>` | 0 rows (invisible by primary key, not merely absent from a list) |
   | `UPDATE ... WHERE <pk> = <B's pk>` | `UPDATE 0` |
   | `INSERT` stamped with B's tenant id | `42501 new row violates row-level security policy` |

4. With **no** GUC set: `SELECT` returns 0 and `INSERT` is rejected. (Fail-closed; proven achievable
   in Part 1.2.)
5. `ROLLBACK`.

The `INSERT`-with-B's-id case is the one hand-written tests routinely omit and it is the one that
catches a missing `WITH CHECK`.

**The census assertion is what makes this deletion-proof.** One extra test asserts that the number of
cases the theory executed equals the number of `TenantOwned` + `DerivedFromParent` rows in the
manifest. Deleting a case is then impossible without editing the manifest, and editing the manifest
is a reviewable one-line diff that says which table stopped being isolated. That is the specific
control the deleted test lacked: its coverage was implicit in its existence.

Realistic cost: ~200 cases × (2 inserts + 4 assertions) in rolled-back transactions on a shared
container — a few minutes, one CI job, nightly and on migration changes rather than on every PR.

### Tier 3 — Journey (hand-written, ~12 spine tables)

Tier 2 proves the *database* refuses. It cannot prove the *application* asks the right question — it
never issues an HTTP request. For the RFQ→ZATCA spine (`Leads`, `RFQ`, `Quotes`, `Orders`,
`Shipments`, `CommercialCases`, `Customers`, `Suppliers`, `Products`, `customer_identifiers`,
`supplier_quotes`, `Users`), keep hand-written HTTP tests that sign in as tenant A and request
tenant B's resource by id, asserting 404/403 — never 200-with-empty-body, which hides an authorization
bug behind a filter.

Tier 3 is the only tier where deletion is a real risk, and 12 files is a small enough surface for the
diff sweep that caught tonight's deletion to keep watching.

### The one-line standard

> **Every table the manifest classes `TenantOwned` or `DerivedFromParent` must have (a) a catalogue
> assertion that its policy is exactly the generated expected policy, and (b) a generated behavioural
> case proving cross-tenant read, update and insert are all refused. Coverage is counted against the
> manifest, so a test cannot be removed without removing the table's declaration.**

---

## Part 7 — What to do about the `BYPASSRLS` roles

`BYPASSRLS` is a role attribute, not a policy. It cannot be scoped, audited or narrowed. It is on or
off, and while it is on the database contributes nothing to isolation. Two of the three application
roles have it.

### The measurement

```sql
-- RLS tables each bypass role can reach, that no policy would admit it to if BYPASSRLS were dropped
SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind='r' AND n.nspname IN ('public','platform') AND c.relrowsecurity
  AND (has_table_privilege('<role>', c.oid,'SELECT') OR has_table_privilege('<role>', c.oid,'INSERT'))
  AND NOT EXISTS (SELECT 1 FROM pg_policy p WHERE p.polrelid = c.oid
                    AND ('<role>'::regrole = ANY(p.polroles) OR p.polroles = '{0}'));
```

- `nexora_pipeline_app`: **195 tables**. Dropping `BYPASSRLS` today breaks 195 tables.
- `nexora_identity_app`: **3 tables** — `public."Users"`, `public."BusinessUnits"`,
  `public."Setup_Master"`. Everything else it touches (`public."LoginAttempts"`,
  `platform."TenantAdminInvitations"`) has no RLS at all, so a grant is sufficient.

Those two numbers say the two roles need completely different treatment.

### Step 1 (do this) — retire `nexora_identity_app`'s `BYPASSRLS`

Three tables is a tractable declaration. Proven live, in a transaction that was rolled back — the
role attribute and all 232 policies were verified restored afterwards:

```
--- identity role TODAY (BYPASSRLS) ---
identity sees Users:|2|ann@a.example,bob@b.example

--- identity role WITHOUT BYPASSRLS and no identity policy ---
identity sees Users:|0

--- identity role WITHOUT BYPASSRLS, with one explicit declared policy ---
CREATE POLICY nexora_identity_login_lookup ON "Users" FOR SELECT TO nexora_identity_app USING (true);
identity sees Users:|2|ann@a.example,bob@b.example

--- and the tenant role is still confined ---
tenant 9101 sees Users:|1|ann@a.example
```

`USING (true)` looks like it changes nothing, and functionally it does not. What changes is that the
reach becomes **declared, per-table, per-command and reviewable**. `FOR SELECT` means the login
lookup cannot write. A reviewer reading
`nexora_identity_login_lookup ON "Users" FOR SELECT TO nexora_identity_app USING (true)` can see the
entire cross-tenant surface of the identity plane on three lines. Today they must infer it from a
role attribute and 226 grant rows.

Tighten further where the flow allows — `Users` could be
`USING ("IsActive" AND "DeactivatedAtUtc" IS NULL)`, so a login lookup cannot resolve a deactivated
account even if the application forgets to check.

This closes branches D and E of F2's decision table: they still route to `nexora_identity_app`, but
that role is then bound by three declared policies instead of being unbound everywhere.

### Step 2 (stage this) — shrink `nexora_pipeline_app`

195 tables is not a policy-writing exercise, it is a decomposition. The role is doing at least four
unrelated jobs: platform fleet metering, background workers over tenant data, the anonymous platform
login path, and provisioning. Split it:

| Successor | Reach | `BYPASSRLS`? |
|---|---|---|
| `nexora_platform_app` | `platform` schema only | **no** — the 10 `platform_fleet` policies already exist, `USING (true) TO nexora_pipeline_app`. Re-point them and the role works unchanged. |
| `nexora_worker_app` | tenant tables, one tenant at a time | **no** — workers loop tenants anyway; have them `SET LOCAL ROLE nexora_tenant_app` and set the GUC per iteration. The existing tenant policies then apply with no new policy at all. |
| `nexora_provisioning_app` | tenant bootstrap (writes the first rows of a new tenant) | yes, initially — genuinely creates rows before a tenant scope exists |
| `nexora_login_app` | `LoginAttempts`, `PlatformUsers`, `PlatformMfa*`, `PlatformSessions` | **no** — none of those tables has RLS |

The 10 existing `platform_fleet` policies are the evidence that this is the intended direction: they
are inert today (the role bypasses them), which means someone already wrote the policies that a
non-bypass pipeline role would need. Finishing that work is cheaper than it looks.

**Sequencing.** Do not revoke `BYPASSRLS` and hope. For each role: add the declared policies first,
then run the Tier 2 suite with the role's `BYPASSRLS` revoked inside a rolled-back transaction (the
technique proven above), and only make the revocation permanent once that passes. That gives a
zero-risk dry run of every step.

### Step 3 — the floor

Whatever the end state, three properties should be permanently asserted by G9:

1. No role with `BYPASSRLS` may also have `LOGIN`. Bypass must always require a deliberate
   `SET LOCAL ROLE`.
2. The set of `BYPASSRLS` roles is a checked-in list with a written reason per role. Adding one is a
   reviewed diff.
3. `ValidateRuntimeDatabaseRoleAsync` should additionally assert that the runtime login role does not
   own tenant tables — or, if it must (because migrations reuse it), that every tenant table is
   `FORCE`. That is F1's remedy expressed as a startup invariant, and it means the application refuses
   to boot into the configuration that leaks.

---

## Appendix — reproducing the proofs

All of the following were run against `nexora-squash-a` on 2026-08-11 and left no residue.

**F1, direction 1 and 2** — throwaway database `rls_force_proof` / `rls_purge_proof` with roles
`proof_owner` (`LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS`) and `proof_tenant_app`, two tables
identical except for `FORCE`. Dropped afterwards. Key results: owner sees 2 of 2 rows on the
`ENABLE`-only table and 0 of 2 on the `FORCE` table; owner `DELETE` removes 5 rows from the
`ENABLE`-only table and 0 from the `FORCE` table, without error.

**F3** — inside `BEGIN … ROLLBACK` on `nexora_a`: two business units, two customers, then
`INSERT INTO customer_identifiers ("BusinessUnitId","CustomerId",…) VALUES (9001, 9002, …)` →
`INSERT 0 1`.

**Tenant isolation works as designed** — same transaction: tenant `9001` sees `Acme (tenant 9001)`
only; tenant `9002` sees `Globex (tenant 9002)` only.

**F4** — `SET LOCAL ROLE nexora_tenant_app; SELECT * FROM platform."PlatformEmailSettings"` →
`permission denied`; `SELECT "SmtpPassword","SendGridApiKey" FROM platform."PlatformEmailSettings"` →
succeeds.

**F6** — `SET LOCAL ROLE nexora_tenant_app; SELECT count(*) FROM "LedgerActorNonces"` →
`ERROR: permission denied for table LedgerActorNonces`.

**Part 7 Step 1** — inside `BEGIN … ROLLBACK`: `ALTER ROLE nexora_identity_app NOBYPASSRLS` then
`CREATE POLICY nexora_identity_login_lookup`. Post-rollback verification:
`nexora_identity_app.rolbypassrls = true`, `count(*) FROM pg_policies = 232`, `count(*) FROM "Users" = 0`.

### Files referenced

- `Backend/ERP_RFQ_Automation/MultiTenancy/TenantRlsCommandInterceptor.cs` — `ResolveDatabaseRole`, lines 235–306
- `Backend/ERP_RFQ_Automation/Program.cs` — `ValidateRuntimeDatabaseRoleAsync` (892), `FallbackPolicy` (475)
- `Backend/ERP_RFQ_Automation/Platform/Lifecycle/TenantPurgeExecutor.cs` — lines 222–247, 507–517
- `Backend/ERP_RFQ_Automation/Platform/Controllers/PlatformAuthController.cs` — `[AllowAnonymous]` at 35 and 116
- `Backend/ERP_RFQ_Automation/Migrations/20260723120000_CompleteTenantRlsCoverage.cs` — the original sweep
- `Backend/ERP_RFQ_Automation/MigrationsBaseline/Sql/08_row_level_security.sql` — 232 literal policies
- `Backend/ERP_RFQ_Automation/MigrationsBaseline/Sql/03_tables_and_sequences.sql` — 111 literal `FORCE`
- `Backend/ERP_RFQ_Automation/MigrationsBaseline/regenerate-baseline-sql.py` — extend for R3
- `Backend/ERP_RFQ_Automation.Tests/Support/schema-parity-queries.sql` — the reader half of the gate
- `Backend/ERP_RFQ_Automation.Tests/PostgreSqlProductionDialectTests.cs` — existing catalogue assertions
