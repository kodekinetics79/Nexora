#!/usr/bin/env python3
"""Regenerate MigrationsBaseline/Sql/ from a schema-only pg_dump.

USAGE
    1. Build the reference database by applying ALL the pre-baseline migrations to a
       fresh PostgreSQL 16 instance, in order:

           dotnet ef migrations script --no-build -o /tmp/all.sql   # on the pre-squash tree
           psql "$REF" -v ON_ERROR_STOP=1 -f /tmp/all.sql

    2. Dump it, privileges included, ownership excluded:

           pg_dump -d "$REF" --schema-only --no-owner > /tmp/reference-schema.sql

    3. Regenerate:

           python3 regenerate-baseline-sql.py /tmp/reference-schema.sql ./Sql

    4. Re-verify: apply the baseline to a second fresh database and diff both with
       ERP_RFQ_Automation.Tests/Support/schema-parity-queries.sql. The bar is an
       EMPTY diff in both directions.

    Two files are NOT produced here and are preserved across runs:
      11_reference_data.sql - the public."Module" catalogue the migrations seed, which a
                              schema-only dump cannot see
      90_down.sql           - the baseline's teardown, generated from the same reference
                              database's catalogue

Turn a `pg_dump --schema-only --no-owner` into the baseline migration's SQL.

The dump IS the specification: replaying it reproduces DB_A by construction rather
than by transcription. This script only does the handful of edits that a dump needs
before it can run inside an EF migration:

  * strip psql meta-commands (\\restrict / \\unrestrict) - Npgsql cannot parse them
  * make the session GUCs transaction-local so the migration cannot leak
    search_path='' onto a pooled application connection
  * drop the __EFMigrationsHistory CREATE TABLE + PK - EF's own history repository
    creates that table before the migration runs
  * drop COMMENT ON EXTENSION - set automatically by CREATE EXTENSION, and requires
    extension ownership that a managed-Postgres migration role may not have
  * make every statement IDEMPOTENT (see below)

Nothing else is rewritten. In particular no GRANT, POLICY, TRIGGER, RLS flag or
CHECK constraint is touched: idempotency is added by GUARDING a statement, never by
changing what the statement does.

IDEMPOTENCY - WHY, AND WHAT THE GUARD IS
    A pg_dump replay assumes an empty database. The deployed database is not empty:
    Render was serving a container 40 commits old, so production is still at the
    PRE-squash head (134 __EFMigrationsHistory rows, full schema materialised) and
    does NOT have 20260811033109_SquashedSchemaBaseline in its history. Program.cs
    calls MigrateAsync() uncaught before the app serves, so EF applies the baseline
    to a database that already has every object in it, the first bare CREATE raises
    42P06/42P07, the process dies at boot, the deploy is marked failed and the old
    container keeps serving. That is a deadlock the deploy cannot leave on its own.

    MigrationsBaseline/stamp-existing-database.sql fixes it by rewriting the history
    instead, and is still the cleaner route - but it needs a human holding production
    credentials to run it at exactly the right moment. Guarding the baseline removes
    that dependency: the deploy heals itself.

    Every statement therefore ends up in one of three states, and `validate()` below
    FAILS THE GENERATOR if a statement is in none of them:

      already idempotent   SET LOCAL, GRANT, REVOKE, CREATE EXTENSION IF NOT EXISTS,
                           ALTER TABLE ... {ENABLE,FORCE} ROW LEVEL SECURITY,
                           ALTER TABLE ... ENABLE ALWAYS TRIGGER, INSERT ... ON
                           CONFLICT DO NOTHING, DROP ... IF EXISTS
      native IF NOT EXISTS CREATE TABLE / SEQUENCE / INDEX / SCHEMA, and
                           CREATE OR REPLACE FUNCTION
      catalogue guard      objects PostgreSQL 16 gives no IF NOT EXISTS form -
                           constraints, foreign keys, triggers, policies and identity
                           columns - wrapped in
                               DO $nexora_idem$ BEGIN
                                   IF NOT EXISTS (SELECT 1 FROM pg_catalog...) THEN
                                       <the statement, byte for byte>;
                                   END IF;
                               END $nexora_idem$;

    The failure mode to fear is the opposite of the one that broke the deploy: a
    guard that silently SKIPS an object that should have been created leaves a table
    with no RLS policy and no error. Two things hold that line. The guards key on the
    exact identity of the object (conname+conrelid, tgname+tgrelid, polname+polrelid,
    attname+attrelid) so a guard can only skip the object it names; and the generator
    refuses to emit a statement it does not recognise, so a new object type cannot
    slip through unguarded and unnoticed. The counts are asserted downstream, by
    SquashedBaselineIdempotencyPostgreSqlTests: 232 policies, 110 FORCE, 300 triggers,
    142 functions, 2 EXCLUDE constraints, after applying the baseline to a database
    that already had all of them.
"""
import re
import sys
from pathlib import Path

if len(sys.argv) != 3:
    raise SystemExit(__doc__)
DUMP = Path(sys.argv[1])
OUT_DIR = Path(sys.argv[2])

# (filename, human title, first `-- Name:` type that opens the chunk)
PHASES = [
    ('02_functions.sql', 'Functions (142; 36 SECURITY DEFINER)', 'FUNCTION'),
    ('03_tables_and_sequences.sql', 'Tables, identity sequences, FORCE ROW LEVEL SECURITY flags', 'TABLE'),
    ('04_constraints.sql', 'Primary keys, unique keys, CHECK and EXCLUDE constraints', 'CONSTRAINT'),
    ('05_indexes.sql', 'Indexes (incl. partial and expression indexes)', 'INDEX'),
    ('06_triggers.sql', 'Triggers (incl. ENABLE ALWAYS)', 'TRIGGER'),
    ('07_foreign_keys.sql', 'Foreign keys (incl. NOT VALID)', 'FK CONSTRAINT'),
    ('08_row_level_security.sql', 'ENABLE ROW LEVEL SECURITY + policies', 'ROW SECURITY'),
    ('09_privileges.sql', 'Schema / table / column / sequence / function privileges', 'ACL'),
]

HEADER_RE = re.compile(r'^-- Name: (.*); Type: ([A-Z ]+); Schema: (.*); Owner: (.*)$')

PREAMBLE = """\
-- ---------------------------------------------------------------------------
-- Execution roles.
--
-- Transcribed verbatim from the migrations that created them, because pg_dump
-- never emits roles (they are cluster-scoped, not database-scoped) and every
-- GRANT and every `TO nexora_tenant_app` policy below depends on them existing.
--
--   nexora_tenant_app    20260723031900_AddTenantRowLevelSecurity
--   nexora_identity_app  20260728134117_ConfigureDatabaseExecutionRoles
--   nexora_pipeline_app  20260728134117_ConfigureDatabaseExecutionRoles
--
-- NOINHERIT on all three, and on the migrating role itself, is the control that
-- forces an explicit SET ROLE instead of silent privilege inheritance
-- (20260723140000_AddAiGovernanceLedger). BYPASSRLS is deliberately asymmetric:
-- the tenant role is NOBYPASSRLS (it is the role RLS is written against), the
-- identity and pipeline roles are BYPASSRLS.
-- ---------------------------------------------------------------------------
DO $roles$
DECLARE runtime_role name;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
        CREATE ROLE nexora_tenant_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
        CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
        CREATE ROLE nexora_pipeline_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
    END IF;

    EXECUTE format('GRANT nexora_tenant_app TO %I', current_user);
    EXECUTE format('GRANT nexora_identity_app, nexora_pipeline_app TO %I', current_user);
    FOR runtime_role IN
        SELECT rolname
        FROM pg_roles
        WHERE rolcanlogin
          AND NOT rolinherit
          AND NOT rolsuper
          AND NOT rolbypassrls
          AND pg_has_role(oid, 'nexora_tenant_app', 'MEMBER')
    LOOP
        EXECUTE format(
            'GRANT nexora_identity_app, nexora_pipeline_app TO %I', runtime_role);
    END LOOP;

    EXECUTE format('ALTER ROLE %I NOINHERIT', current_user);
END
$roles$;
"""


REVOKES = """\
-- ---------------------------------------------------------------------------
-- Explicit REVOKEs transcribed verbatim from the migrations that issued them.
--
-- pg_dump cannot emit these: revoking a privilege a role never held leaves the
-- table with no non-owner ACL entry, so the dump has nothing to print. The
-- statements are still replayed because they are the recorded intent that the
-- migration history holds, and because issuing them materialises the same
-- pg_class.relacl state DB_A has (owner-default entries present rather than
-- relacl = NULL), which is what a catalogue-level parity check compares.
--
--   20260723120000_CompleteTenantRlsCoverage
--   20260723230000_GovernStatementsAndDunning
--   20260728134117_ConfigureDatabaseExecutionRoles
-- ---------------------------------------------------------------------------
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory" FROM nexora_tenant_app;
REVOKE ALL ON public."FinanceProviderSecrets" FROM PUBLIC;
REVOKE ALL ON public."FinanceProviderSecrets" FROM nexora_tenant_app;
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory"
    FROM nexora_identity_app;
REVOKE ALL PRIVILEGES ON TABLE public."__EFMigrationsHistory", public."FinanceProviderSecrets"
    FROM nexora_pipeline_app;
REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
    FROM nexora_pipeline_app;

-- Put search_path back before control returns to EF Core. The replay above ran with
-- pg_dump's empty search_path; EF's own `INSERT INTO "__EFMigrationsHistory"` runs in
-- this same transaction and is not schema-qualified, so it would fail without this.
SELECT pg_catalog.set_config(
    'search_path',
    current_setting('nexora.squashed_baseline_saved_search_path'),
    true);
"""

# DB_A carries one dropped-column tombstone: public."CommercialMatchingPolicies"
# attnum 10 was added and later dropped across the 134 migrations, so the two
# columns after it sit at attnum 11 and 12. pg_dump never emits dropped columns,
# so a straight replay would place them at 10 and 11. Recreate the gap so column
# ordinals match DB_A exactly.
TOMBSTONE_TABLE = 'public."CommercialMatchingPolicies"'
TOMBSTONE_TAIL = ['OutputTaxRatePercent', 'SupplierInputTaxRecoverablePercent']
TOMBSTONE_SQL = """
-- Ordinal parity: DB_A has a dropped-column tombstone at attnum 10 of this table
-- (a column added and later dropped across the 134 pre-baseline migrations).
-- Recreate the gap so "OutputTaxRatePercent" and "SupplierInputTaxRecoverablePercent"
-- land on attnum 11 and 12 as they do in DB_A. Purely an ordinal artefact - it has
-- no effect on any query, since EF always names its columns.
--
-- The whole block is guarded as ONE unit, on the presence of the first of the two
-- columns, and NOT column-by-column with ADD COLUMN IF NOT EXISTS. Adding a column
-- and dropping it again is not a no-op on a database that already has this table:
-- it leaves a SECOND pg_attribute tombstone behind and pushes every later column's
-- attnum along by one. On a database that already has the schema this block must do
-- literally nothing.
DO $nexora_tombstone$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_attribute
        WHERE attrelid = to_regclass('public."CommercialMatchingPolicies"')
          AND attname = 'OutputTaxRatePercent'
          AND NOT attisdropped
    ) THEN
        ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "__squashed_baseline_ordinal_gap" boolean;
        ALTER TABLE public."CommercialMatchingPolicies" DROP COLUMN "__squashed_baseline_ordinal_gap";
        ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "OutputTaxRatePercent" numeric(9,4) DEFAULT 15.0;
        ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "SupplierInputTaxRecoverablePercent" numeric(9,4) DEFAULT 100.0 NOT NULL;
    END IF;
END
$nexora_tombstone$;
"""


# pg_dump prints a CHECK expression from the parse tree, and five of them do not
# round-trip: `x BETWEEN a AND b` and row-wise `(a,b) IN ((..),(..))` build BoolExpr
# nodes that the grammar does NOT flatten, whereas re-parsing pg_dump's expanded
# `(x >= a) AND (x <= b)` form does flatten. The stored trees are logically identical
# but `pg_get_constraintdef(oid)` (non-pretty) prints different parenthesisation, so
# a byte-exact parity check sees a difference. Restore the original migration source
# for exactly these five - the text is transcribed verbatim from the migration named
# beside each one.
CHECK_SOURCE_OVERRIDES = {
    # 20260723235000_AddGovernedGeneralLedger
    'CK_AccountingPeriods_Range':
        '"FiscalYear" BETWEEN 2000 AND 2200 AND "PeriodNumber" BETWEEN 1 AND 99 '
        'AND "StartsOn" <= "EndsOn"',
    # 20260724003000_GovernTreasuryRulesAdjustmentsAndCashBridge (re-added form)
    'CK_LedgerBooks_State':
        '"FiscalYearStartMonth" BETWEEN 1 AND 12 AND "Version" > 0',
    # 20260730050411_Wave1AiTrustPolicy
    'CK_AiProcessingPolicies_TrustControls':
        '"ExternalDependencyCeilingPercent" BETWEEN 0 AND 10\n'
        '        AND "RetentionDays" BETWEEN 1 AND 3650\n'
        '        AND length(trim("AllowedDataClassifications")) > 0\n'
        '        AND length(trim("EgressPolicy")) > 0\n'
        '        AND length(trim("DataResidency")) > 0\n'
        '        AND (NOT "ExternalProcessingAllowed"\n'
        '             OR ("RedactionRequired" AND "PrivacyReviewRequired"\n'
        '                 AND "AllowedProvider" IS NOT NULL AND "AllowedModel" IS NOT NULL))',
    # 20260808190127_Wave6PlatformFoundations
    'CK_UsageEvents_Meter':
        '("EventType","Unit") IN (\n'
        "        ('processing.minutes','minute'),('documents','document'),('pages.processed','page'),\n"
        "        ('rfqs','rfq'),('quotes','quote'),('orders','order'),('emails','email'),\n"
        "        ('pages.ocr','page'),('ai.tokens','token'),('api.calls','call'),\n"
        "        ('storage.gb-hours','gb-hour'),('supplier.searches','search'),('automation.runs','run'),\n"
        "        ('base.subscription','subscription'),('users','user'),('dedicated.infrastructure','instance'))",
    # 20260808234734_FinalPlatformRevenueControls
    'CK_SubscriptionTaxRules_RateIntervalEvidence':
        '"RatePercent" BETWEEN 0 AND 100\n'
        '        AND ("EffectiveToUtc" IS NULL OR "EffectiveToUtc">"EffectiveFromUtc")\n'
        "        AND \"EvidenceSha256\" ~ '^[0-9a-f]{64}$'",
}


def restore_check_sources(text, stats):
    for name, source in CHECK_SOURCE_OVERRIDES.items():
        marker = f'CONSTRAINT "{name}" CHECK '
        idx = text.find(marker)
        if idx < 0:
            raise SystemExit(f'CHECK override {name!r} not found in dump')
        open_paren = idx + len(marker)
        if text[open_paren] != '(':
            raise SystemExit(f'CHECK override {name!r}: unexpected shape')
        close_paren = match_forwards(text, open_paren)
        text = text[:open_paren] + '(' + source + ')' + text[close_paren + 1:]
        stats['check_source'] += 1
    return text


def rewrite_array_casts(text, stats):
    """Make pg_dump's array-coercion deparse round-trip.

    pg_dump prints `("Col")::text = ANY ((ARRAY['a'::character varying, ...])::text[])`.
    Re-parsing that distributes the cast over the elements, so PostgreSQL stores a
    different (semantically identical) parse tree and pg_get_constraintdef /
    pg_get_indexdef then print `ARRAY[('a'::character varying)::text, ...]` instead.
    The source form that round-trips is the one the original migrations used:
    `"Col" = ANY (ARRAY['a'::character varying, ...])`, i.e. `"Col" IN (...)`.
    """
    for op in (' = ANY ((ARRAY[', ' <> ALL ((ARRAY['):
        marker = ')::text' + op
        while True:
            idx = text.find(marker)
            if idx < 0:
                break
            operand_close = idx                      # the ')' before '::text'
            operand_open = match_backwards(text, operand_close)
            operand = text[operand_open + 1:operand_close]
            arr_open = idx + len(marker) - len('(ARRAY[')   # the '(' of '(ARRAY['
            arr_close = match_forwards(text, arr_open)
            suffix = '::text[])'
            if text[arr_close + 1:arr_close + 1 + len(suffix)] != suffix:
                raise SystemExit(f'unexpected array-cast shape at offset {idx}')
            array_text = text[arr_open + 1:arr_close]      # 'ARRAY[...]'
            opname = op[:op.index('(')].rstrip()           # '= ANY' / '<> ALL'
            replacement = f'{operand} {opname} ({array_text})'
            end = arr_close + 1 + len(suffix)
            text = text[:operand_open] + replacement + text[end:]
            stats['array_cast'] += 1
    return text


def match_backwards(text, close_idx):
    depth = 0
    i = close_idx
    while i >= 0:
        ch = text[i]
        if ch == ')':
            depth += 1
        elif ch == '(':
            depth -= 1
            if depth == 0:
                return i
        i -= 1
    raise SystemExit('unbalanced parentheses scanning backwards')


def match_forwards(text, open_idx):
    depth = 0
    i = open_idx
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 1
        elif ch == '(':
            depth += 1
        elif ch == ')':
            depth -= 1
            if depth == 0:
                return i
        i += 1
    raise SystemExit('unbalanced parentheses scanning forwards')


def apply_tombstone(body, stats):
    """Strip the two trailing columns from the CommercialMatchingPolicies CREATE
    TABLE and re-add them after a dropped-column tombstone."""
    out = []
    i = 0
    n = len(body)
    while i < n:
        line = body[i]
        if line.startswith('CREATE TABLE ' + TOMBSTONE_TABLE):
            block = [line]
            i += 1
            while i < n and not body[i].rstrip().endswith(');'):
                block.append(body[i])
                i += 1
            block.append(body[i])
            i += 1
            kept, moved_constraints = [], []
            for b in block:
                stripped = b.strip()
                if any(stripped.startswith(f'"{c}" ') for c in TOMBSTONE_TAIL):
                    continue                       # column moves to an ALTER TABLE
                if stripped.startswith('CONSTRAINT ') and any(
                        f'"{c}"' in b for c in TOMBSTONE_TAIL):
                    moved_constraints.append(stripped.rstrip(','))
                    continue                       # constraint follows its columns
                kept.append(b)
            # the line before the closing ');' must not end with a comma
            for j in range(len(kept) - 2, 0, -1):
                if kept[j].rstrip().endswith(','):
                    kept[j] = kept[j].rstrip()[:-1]
                    break
            out.extend(kept)
            out.extend(TOMBSTONE_SQL.split('\n'))
            for con in moved_constraints:
                out.append(f'ALTER TABLE public."CommercialMatchingPolicies" ADD {con};')
            out.append('')
            stats['tombstone'] += 1
            continue
        out.append(line)
        i += 1
    return out


# =============================================================================
# Idempotency
# =============================================================================

GUARD_TAG = '$nexora_idem$'

DOLLAR_TAG_RE = re.compile(r'\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$')


def iter_statements(text):
    """Split SQL into top-level statements.

    Yields the slices of `text` that, concatenated in order, reproduce `text`
    exactly - so a rewriter that returns a statement unchanged is a no-op on the
    file. A statement carries the comments and blank lines that precede it, which
    is what keeps pg_dump's `-- Name: ...; Type: ...` header attached to the object
    it documents.

    Single quotes, double-quoted identifiers, dollar-quoted bodies ($$, $_$,
    $nexora_tombstone$) and -- line comments are all skipped over, so a semicolon
    inside a function body or inside a CHECK expression's string literal does not
    end a statement.
    """
    i = 0
    n = len(text)
    start = 0
    while i < n:
        ch = text[i]
        if ch == '-' and text.startswith('--', i):
            j = text.find('\n', i)
            i = n if j < 0 else j + 1
            continue
        if ch == "'":
            i += 1
            while i < n:
                if text[i] == "'":
                    if text.startswith("''", i):
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            continue
        if ch == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 1
            i += 1
            continue
        if ch == '$':
            m = DOLLAR_TAG_RE.match(text, i)
            if m:
                tag = m.group(0)
                j = text.find(tag, i + len(tag))
                if j < 0:
                    raise SystemExit(f'unterminated dollar-quoted string opened at offset {i}')
                i = j + len(tag)
                continue
        if ch == ';':
            yield start, i + 1
            start = i + 1
        i += 1
    if start < n:
        yield start, n


def split_lead(chunk):
    """Separate the leading comments/whitespace from the SQL of one statement."""
    i = 0
    n = len(chunk)
    while i < n:
        if chunk[i].isspace():
            i += 1
            continue
        if chunk.startswith('--', i):
            j = chunk.find('\n', i)
            i = n if j < 0 else j + 1
            continue
        break
    return chunk[:i], chunk[i:]


def literal(name):
    """A quoted-identifier's text as a SQL string literal."""
    return "'" + name.replace('"', '').replace("'", "''") + "'"


def regclass(qualified):
    """`public."Foo"` -> `to_regclass('public."Foo"')`.

    to_regclass and not `::regclass`: a missing table yields NULL, the guard then
    matches nothing, the statement runs and PostgreSQL raises its own readable
    "relation does not exist". A ::regclass cast would raise 42P01 from inside the
    guard instead and name the wrong culprit.
    """
    return "to_regclass('" + qualified.replace("'", "''") + "')"


def guard(lead, sql, condition, reason):
    """Wrap one statement in an existence check, byte for byte.

    The statement is NOT re-indented. Re-indenting would be safe for everything the
    dump currently emits, but it stops being safe the moment a CHECK expression or a
    policy predicate carries a multi-line string literal, and this file is generated
    from whatever the catalogue holds next year too.
    """
    if GUARD_TAG in sql:
        raise SystemExit(f'guard tag collides with statement text: {sql[:120]!r}')
    return (f'{lead}DO {GUARD_TAG}\nBEGIN\n'
            f'-- {reason}\nIF NOT EXISTS (\n    {condition}\n) THEN\n'
            f'{sql}\n'
            f'END IF;\nEND\n{GUARD_TAG};\n')


ADD_CONSTRAINT_RE = re.compile(
    r'^ALTER TABLE (?:ONLY )?(?P<table>\S+)\s+ADD CONSTRAINT (?P<name>"[^"]+"|\S+) ', re.S)
CREATE_TRIGGER_RE = re.compile(
    r'^CREATE (?:CONSTRAINT )?TRIGGER (?P<name>"[^"]+"|\S+)\b.*?\sON (?P<table>\S+?)(?=[\s(])', re.S)
CREATE_POLICY_RE = re.compile(
    r'^CREATE POLICY (?P<name>"[^"]+"|\S+) ON (?P<table>\S+?)(?=[\s;])', re.S)
ADD_IDENTITY_RE = re.compile(
    r'^ALTER TABLE (?:ONLY )?(?P<table>\S+) ALTER COLUMN (?P<column>"[^"]+"|\S+) ADD GENERATED ', re.S)


def make_idempotent(text, stats):
    """Rewrite one generated file so replaying it onto a database that already has
    the schema changes nothing and raises nothing."""
    out = []
    for start, end in iter_statements(text):
        lead, sql = split_lead(text[start:end])
        stripped = sql.strip()
        if not stripped:
            out.append(text[start:end])
            continue

        # --- native IF NOT EXISTS / OR REPLACE forms --------------------------
        if stripped.startswith('CREATE SCHEMA '):
            sql = sql.replace('CREATE SCHEMA ', 'CREATE SCHEMA IF NOT EXISTS ', 1)
            stats['idem_schema'] += 1
        elif stripped.startswith('CREATE TABLE '):
            sql = sql.replace('CREATE TABLE ', 'CREATE TABLE IF NOT EXISTS ', 1)
            stats['idem_table'] += 1
        elif stripped.startswith('CREATE SEQUENCE '):
            sql = sql.replace('CREATE SEQUENCE ', 'CREATE SEQUENCE IF NOT EXISTS ', 1)
            stats['idem_sequence'] += 1
        elif stripped.startswith('CREATE FUNCTION '):
            # OR REPLACE rather than a catalogue guard, deliberately. A guard would
            # leave a drifted body in place; OR REPLACE restores the body the 134
            # migrations produced. It keeps the OID and the ACL, so 09_privileges.sql
            # still lands on the same function, and pg_dump output is unchanged.
            sql = sql.replace('CREATE FUNCTION ', 'CREATE OR REPLACE FUNCTION ', 1)
            stats['idem_function'] += 1
        elif stripped.startswith('CREATE INDEX ') or stripped.startswith('CREATE UNIQUE INDEX '):
            sql = sql.replace('INDEX ', 'INDEX IF NOT EXISTS ', 1)
            stats['idem_index'] += 1

        # --- catalogue guards: no IF NOT EXISTS form exists in PostgreSQL 16 --
        elif ADD_CONSTRAINT_RE.match(stripped):
            m = ADD_CONSTRAINT_RE.match(stripped)
            sql = guard(
                '', sql.rstrip('\n'),
                f'SELECT 1 FROM pg_constraint\n'
                f'    WHERE conname = {literal(m.group("name"))}\n'
                f'      AND conrelid = {regclass(m.group("table"))}',
                'No ADD CONSTRAINT IF NOT EXISTS in PostgreSQL: guarded on pg_constraint.')
            stats['idem_constraint'] += 1
        elif CREATE_TRIGGER_RE.match(stripped):
            m = CREATE_TRIGGER_RE.match(stripped)
            sql = guard(
                '', sql.rstrip('\n'),
                f'SELECT 1 FROM pg_trigger\n'
                f'    WHERE tgname = {literal(m.group("name"))}\n'
                f'      AND tgrelid = {regclass(m.group("table"))}\n'
                f'      AND NOT tgisinternal',
                'No CREATE TRIGGER IF NOT EXISTS in PostgreSQL: guarded on pg_trigger.')
            stats['idem_trigger'] += 1
        elif CREATE_POLICY_RE.match(stripped):
            m = CREATE_POLICY_RE.match(stripped)
            sql = guard(
                '', sql.rstrip('\n'),
                f'SELECT 1 FROM pg_policy\n'
                f'    WHERE polname = {literal(m.group("name"))}\n'
                f'      AND polrelid = {regclass(m.group("table"))}',
                'No CREATE POLICY IF NOT EXISTS in PostgreSQL: guarded on pg_policy.')
            stats['idem_policy'] += 1
        elif ADD_IDENTITY_RE.match(stripped):
            m = ADD_IDENTITY_RE.match(stripped)
            sql = guard(
                '', sql.rstrip('\n'),
                f'SELECT 1 FROM pg_attribute\n'
                f'    WHERE attrelid = {regclass(m.group("table"))}\n'
                f'      AND attname = {literal(m.group("column"))}\n'
                f"      AND attidentity <> ''",
                'No ADD GENERATED ... IF NOT EXISTS in PostgreSQL: guarded on pg_attribute.')
            stats['idem_identity'] += 1

        out.append(lead + sql)
    return ''.join(out)


# Statements that are safe to replay as they stand. Anything a generated file
# contains that matches NONE of these, and none of the guarded forms above, aborts
# the generator: an unrecognised object type is exactly how a non-idempotent CREATE
# would slip back in unnoticed.
IDEMPOTENT_AS_IS = [
    re.compile(r'^SET LOCAL '),
    re.compile(r'^SELECT pg_catalog\.set_config\('),
    re.compile(r'^SELECT setval\('),
    re.compile(r'^CREATE EXTENSION IF NOT EXISTS '),
    re.compile(r'^CREATE SCHEMA IF NOT EXISTS '),
    re.compile(r'^CREATE TABLE IF NOT EXISTS '),
    re.compile(r'^CREATE SEQUENCE IF NOT EXISTS '),
    re.compile(r'^CREATE OR REPLACE FUNCTION '),
    re.compile(r'^CREATE (?:UNIQUE )?INDEX IF NOT EXISTS '),
    re.compile(r'^ALTER TABLE (?:ONLY )?\S+ (?:ENABLE|FORCE|NO FORCE|DISABLE) ROW LEVEL SECURITY;$', re.S),
    re.compile(r'^ALTER TABLE (?:ONLY )?\S+ ENABLE (?:ALWAYS|REPLICA) TRIGGER ', re.S),
    re.compile(r'^GRANT '),
    re.compile(r'^REVOKE '),
    re.compile(r'^INSERT INTO .*ON CONFLICT .*DO NOTHING;$', re.S),
    re.compile(r'^DO \$'),
    re.compile(r'^DROP \S+ IF EXISTS ', re.S),
]


def validate(fname, text):
    """Fail the generator if any statement in a produced file is not idempotent."""
    offenders = []
    for start, end in iter_statements(text):
        _, sql = split_lead(text[start:end])
        stripped = sql.strip()
        if not stripped:
            continue
        if not any(rx.match(stripped) for rx in IDEMPOTENT_AS_IS):
            offenders.append(stripped.split('\n')[0][:140])
    if offenders:
        listing = '\n  '.join(offenders[:20])
        raise SystemExit(
            f'{fname}: {len(offenders)} statement(s) are not idempotent and are not '
            f'guarded. Replaying this file onto the deployed database would abort the '
            f'migration and take the boot down with it. Teach make_idempotent() how to '
            f'guard them:\n  {listing}')


def main():
    text = DUMP.read_text(encoding='utf-8')
    lines = text.split('\n')

    # ---- locate the first line of each phase -------------------------------
    starts = {}
    for i, line in enumerate(lines):
        m = HEADER_RE.match(line)
        if not m:
            continue
        typ = m.group(2).strip()
        if typ not in starts:
            starts[typ] = i
    # a `-- Name:` header is preceded by a `--` line; cut there so the comment
    # block stays with its statement
    def cut(i):
        return i - 1 if i > 0 and lines[i - 1].strip() == '--' else i

    bounds = []
    for fname, title, typ in PHASES:
        if typ not in starts:
            raise SystemExit(f'phase type {typ!r} not found in dump')
        bounds.append((fname, title, cut(starts[typ])))
    bounds.sort(key=lambda b: b[2])

    # header chunk = everything before the first phase
    chunks = [('01_schema_and_extensions.sql',
               'Schema + extensions (citext, btree_gist, pgcrypto)',
               0, bounds[0][2])]
    for idx, (fname, title, start) in enumerate(bounds):
        end = bounds[idx + 1][2] if idx + 1 < len(bounds) else len(lines)
        chunks.append((fname, title, start, end))

    out_dir = OUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob('*.sql'):
        if old.name not in ('11_reference_data.sql', '90_down.sql'):
            old.unlink()

    stats = {'restrict': 0, 'set_local': 0, 'efhistory': 0, 'ext_comment': 0,
             'array_cast': 0, 'tombstone': 0, 'check_source': 0,
             'idem_schema': 0, 'idem_table': 0, 'idem_sequence': 0,
             'idem_function': 0, 'idem_index': 0, 'idem_constraint': 0,
             'idem_trigger': 0, 'idem_policy': 0, 'idem_identity': 0}
    manifest = []

    for fname, title, start, end in chunks:
        body = lines[start:end]
        body = scrub(body, stats)
        if fname == '03_tables_and_sequences.sql':
            body = apply_tombstone(body, stats)
        joined = restore_check_sources('\n'.join(body), stats) if fname == '03_tables_and_sequences.sql' else '\n'.join(body)
        body = rewrite_array_casts(joined, stats).split('\n')
        header = [
            '-- ' + '=' * 74,
            f'-- {title}',
            '-- Generated from `pg_dump --schema-only --no-owner` of a database built by',
            '-- applying all 134 pre-baseline migrations in order. Do not hand-edit:',
            '-- regenerate with MigrationsBaseline/regenerate-baseline-sql.py, then re-run',
            '-- the schema-parity diff.',
            '--',
            '-- Every statement is IDEMPOTENT. Production is still at the pre-squash head with',
            '-- the whole schema already materialised, and Program.cs applies migrations',
            '-- uncaught at boot, so a bare CREATE here is a failed deploy. Objects with no',
            '-- IF NOT EXISTS form are wrapped in a DO block that checks pg_catalog for that',
            '-- exact object - never a broader condition that could skip a policy or a',
            '-- constraint the database is genuinely missing.',
            '-- ' + '=' * 74,
            '',
        ]
        content = '\n'.join(header + body).rstrip() + '\n'
        content = make_idempotent(content, stats).rstrip() + '\n'
        (out_dir / fname).write_text(content, encoding='utf-8')
        manifest.append((fname, title, content.count('\n')))

    # the roles preamble and the explicit REVOKEs are hand-authored, not from the dump
    (out_dir / '00_execution_roles.sql').write_text(PREAMBLE, encoding='utf-8')
    manifest.insert(0, ('00_execution_roles.sql', 'Execution roles (nexora_tenant_app / identity / pipeline)',
                        PREAMBLE.count('\n')))
    (out_dir / '10_explicit_revokes.sql').write_text(REVOKES, encoding='utf-8')
    manifest.append(('10_explicit_revokes.sql', 'Explicit REVOKEs (history table, provider secrets, audit log)',
                     REVOKES.count('\n')))

    # Every file the migration replays is checked, including the two that are
    # preserved across runs and the two that are hand-authored above. 90_down.sql is
    # checked too: its DROPs already carry IF EXISTS and this is what keeps them that
    # way, since the Down/Up walk test reruns Up onto whatever Down left behind.
    for fname, _, _ in manifest:
        validate(fname, (out_dir / fname).read_text(encoding='utf-8'))
    for preserved in ('11_reference_data.sql', '90_down.sql'):
        validate(preserved, (out_dir / preserved).read_text(encoding='utf-8'))

    print('scrub stats:', stats)
    total = 0
    for fname, title, n in manifest:
        print(f'  {fname:34s} {n:6d} lines  {title}')
        total += n
    print(f'  {"TOTAL":34s} {total:6d} lines')


def scrub(body, stats):
    out = []
    i = 0
    while i < len(body):
        line = body[i]

        # psql meta-commands - Npgsql cannot parse them
        if line.startswith('\\restrict') or line.startswith('\\unrestrict'):
            stats['restrict'] += 1
            i += 1
            continue

        # session GUCs -> transaction-local, so a pooled connection cannot inherit
        # search_path='' after the migration commits
        if re.match(r'^SET [a-z_]+ = ', line):
            out.append('SET LOCAL ' + line[len('SET '):])
            stats['set_local'] += 1
            i += 1
            continue
        if line.startswith("SELECT pg_catalog.set_config('search_path', '', false);"):
            # pg_dump restores with an empty search_path so that every unqualified
            # name is unresolvable and only its own fully-qualified DDL applies.
            # Keep that, but transaction-locally, and stash the incoming value so the
            # last chunk can put it back: EF appends its own
            # `INSERT INTO "__EFMigrationsHistory"` INSIDE this same transaction, and
            # that insert is NOT schema-qualified. Without the restore the migration
            # dies on its own bookkeeping row.
            out.append("SELECT pg_catalog.set_config("
                       "'nexora.squashed_baseline_saved_search_path', "
                       "current_setting('search_path'), true);")
            out.append("SELECT pg_catalog.set_config('search_path', '', true);")
            stats['set_local'] += 1
            i += 1
            continue

        # COMMENT ON EXTENSION: set automatically by CREATE EXTENSION and requires
        # extension ownership the migrating role may not hold on managed Postgres
        if line.startswith('COMMENT ON EXTENSION '):
            stats['ext_comment'] += 1
            i += 1
            continue

        # __EFMigrationsHistory: EF's history repository creates this table before
        # the migration body runs, so replaying it would fail with "already exists"
        if line.startswith('CREATE TABLE public."__EFMigrationsHistory"'):
            stats['efhistory'] += 1
            while i < len(body) and not body[i].rstrip().endswith(');'):
                i += 1
            i += 1
            out.append('-- CREATE TABLE public."__EFMigrationsHistory" (...) omitted:')
            out.append('-- EF Core\'s HistoryRepository creates it before this migration runs.')
            continue
        if line.startswith('ALTER TABLE ONLY public."__EFMigrationsHistory"'):
            stats['efhistory'] += 1
            while i < len(body) and not body[i].rstrip().endswith(';'):
                i += 1
            i += 1
            out.append('-- ADD CONSTRAINT "PK___EFMigrationsHistory" omitted: created with the table by EF Core.')
            continue

        out.append(line)
        i += 1
    return out


if __name__ == '__main__':
    sys.exit(main())
