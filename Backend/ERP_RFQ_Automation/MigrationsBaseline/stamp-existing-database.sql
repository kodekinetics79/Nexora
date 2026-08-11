-- =====================================================================================
-- stamp-existing-database.sql
--
-- Marks an EXISTING database that already ran all 134 pre-baseline migrations as
-- "already at the squashed baseline", so `Database.Migrate()` does not try to
-- re-create a schema that is already there.
--
-- WHEN YOU NEED THIS
--   Only for a database whose __EFMigrationsHistory holds the 134 pre-baseline
--   rows. A brand-new database needs nothing: the baseline creates everything and
--   writes its own single history row.
--
-- RECOMMENDATION FOR THIS DEPLOYMENT
--   Do not use this. Nexora is pre-launch and holds no real customer data, so the
--   safe move is to DROP AND RECREATE the database and let the baseline build it
--   from scratch. That is the path that has actually been verified end to end
--   (fresh cluster, roles created by the baseline, byte-identical catalogue), and
--   it removes the whole class of "the old database drifted from its migration
--   history" risk in one step. Recreating also re-seeds public."Module", which
--   this script does not touch.
--
--   Keep this script for the case where recreating is not an option - a shared
--   dev database somebody is mid-way through using, or a staging box with
--   hand-loaded fixtures worth keeping.
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
