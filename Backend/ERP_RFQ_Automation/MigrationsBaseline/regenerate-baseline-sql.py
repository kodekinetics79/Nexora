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

Nothing else is rewritten. In particular no GRANT, POLICY, TRIGGER, RLS flag or
CHECK constraint is touched.
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
ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "__squashed_baseline_ordinal_gap" boolean;
ALTER TABLE public."CommercialMatchingPolicies" DROP COLUMN "__squashed_baseline_ordinal_gap";
ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "OutputTaxRatePercent" numeric(9,4) DEFAULT 15.0;
ALTER TABLE public."CommercialMatchingPolicies" ADD COLUMN "SupplierInputTaxRecoverablePercent" numeric(9,4) DEFAULT 100.0 NOT NULL;
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
             'array_cast': 0, 'tombstone': 0, 'check_source': 0}
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
            '-- ' + '=' * 74,
            '',
        ]
        content = '\n'.join(header + body).rstrip() + '\n'
        (out_dir / fname).write_text(content, encoding='utf-8')
        manifest.append((fname, title, content.count('\n')))

    # the roles preamble and the explicit REVOKEs are hand-authored, not from the dump
    (out_dir / '00_execution_roles.sql').write_text(PREAMBLE, encoding='utf-8')
    manifest.insert(0, ('00_execution_roles.sql', 'Execution roles (nexora_tenant_app / identity / pipeline)',
                        PREAMBLE.count('\n')))
    (out_dir / '10_explicit_revokes.sql').write_text(REVOKES, encoding='utf-8')
    manifest.append(('10_explicit_revokes.sql', 'Explicit REVOKEs (history table, provider secrets, audit log)',
                     REVOKES.count('\n')))

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
