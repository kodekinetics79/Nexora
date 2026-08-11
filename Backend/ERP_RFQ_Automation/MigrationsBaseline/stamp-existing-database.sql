-- =====================================================================================
-- stamp-existing-database.sql
--
-- Marks an EXISTING database that already ran all 134 pre-baseline migrations as
-- "already at the squashed baseline", so `Database.Migrate()` does not try to
-- re-create a schema that is already there.
--
-- THE DEPLOY NO LONGER NEEDS THIS. READ THIS FIRST.
--   This script was, for one day, the only thing standing between the squash and a
--   deploy loop nobody could break out of: Render was serving a container 40 commits
--   old, so production sat at the pre-baseline head with the full schema already
--   materialised, Program.cs applied migrations uncaught at boot, the baseline's
--   bare CREATEs raised 42P06/42P07 against objects that already existed, the process
--   died before serving, the deploy failed, and the old container kept serving. The
--   fix for the problem could not deploy because of the problem. Breaking that
--   required a human with production credentials to run this file at exactly the
--   right moment, which is not a property a deploy should have.
--
--   MigrationsBaseline/Sql/*.sql is now idempotent - every CREATE either carries
--   IF NOT EXISTS / OR REPLACE or sits inside a pg_catalog guard - so applying the
--   baseline to a database that already has the schema succeeds and changes nothing.
--   Verified both ways with `pg_dump --schema-only --no-owner`: fresh + idempotent
--   baseline is byte-identical to fresh + the original baseline, and applying it to a
--   production-shaped database (134 history rows, full schema) is a 0-line no-op with
--   232 policies, 110 FORCE, 300 triggers, 142 functions and 2 EXCLUDE constraints
--   still in place. SquashedBaselineIdempotencyPostgreSqlTests holds that line.
--
--   So: deploy normally. Nothing has to be run by hand.
--
-- WHEN YOU STILL WANT THIS
--   Only for a database whose __EFMigrationsHistory holds the 134 pre-baseline rows,
--   and only when you would rather the history be TIDY than merely correct. Letting
--   the idempotent baseline run leaves those 134 stale rows sitting under the
--   baseline's own row - true, but noisy. Stamping replaces them with the single row
--   EF would have written, which is the cleaner end state and skips the replay
--   entirely. A brand-new database needs neither: the baseline creates everything and
--   writes its own row.
--
--   Recreating the database from scratch also remains available and is still the most
--   thorough option while Nexora is pre-launch and holds no real customer data - it
--   removes the "the old database drifted from its migration history" class of risk
--   outright, and re-seeds public."Module", which this script does not touch.
--
-- WHAT IT DOES NOT DO
--   It does not verify that the database's SCHEMA matches the baseline. It only
--   rewrites the bookkeeping. If the database drifted from its migration history,
--   stamping it hides that drift instead of fixing it. Run
--   ERP_RFQ_Automation.Tests/Support/schema-parity-queries.sql against it and
--   against a freshly baselined database first, and diff.
--
-- HOW TO RUN
--   psql "$CONNSTRING" -v ON_ERROR_STOP=1 -f stamp-existing-database.sql
--
-- The whole thing is one transaction and refuses to act unless it finds exactly
-- the state it expects, so a mis-aimed run changes nothing.
-- =====================================================================================

BEGIN;

DO $stamp$
DECLARE
    baseline_id   constant text := '20260811033109_SquashedSchemaBaseline';
    expected_head constant text := '20260810233008_PlatformMfaPolicyAndBrowserTrust';
    expected_rows constant bigint := 134;
    applied_rows  bigint;
    applied_head  text;
    ef_version    text;
BEGIN
    SELECT count(*), max("MigrationId") INTO applied_rows, applied_head
    FROM public."__EFMigrationsHistory";

    -- Already stamped: nothing to do, and re-running must stay safe.
    IF applied_rows = 1 AND applied_head = baseline_id THEN
        RAISE NOTICE 'Already stamped at %; no change.', baseline_id;
        RETURN;
    END IF;

    IF applied_rows <> expected_rows OR applied_head IS DISTINCT FROM expected_head THEN
        RAISE EXCEPTION
            'Refusing to stamp. Expected % rows ending at %, found % rows ending at %. '
            'This database is not at the pre-baseline head, so rewriting its history '
            'would hide whatever it is actually missing.',
            expected_rows, expected_head, applied_rows, coalesce(applied_head, '(none)');
    END IF;

    -- Carry the ProductVersion the existing rows were written with, so the stamped
    -- row is indistinguishable from one EF Core wrote itself.
    SELECT max("ProductVersion") INTO ef_version FROM public."__EFMigrationsHistory";

    DELETE FROM public."__EFMigrationsHistory";
    INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES (baseline_id, coalesce(ef_version, '9.0.9'));

    RAISE NOTICE 'Replaced % pre-baseline rows with %.', expected_rows, baseline_id;
END
$stamp$;

-- Fail the transaction if the end state is not exactly one row naming the baseline.
DO $verify$
DECLARE rows_after bigint; id_after text;
BEGIN
    SELECT count(*), max("MigrationId") INTO rows_after, id_after
    FROM public."__EFMigrationsHistory";
    IF rows_after <> 1 OR id_after <> '20260811033109_SquashedSchemaBaseline' THEN
        RAISE EXCEPTION 'Post-condition failed: % rows, head %.', rows_after, coalesce(id_after, '(none)');
    END IF;
END
$verify$;

COMMIT;
