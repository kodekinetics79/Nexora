using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-MDM-05 immutability. Four separate places in the codebase state that the master-data audit
/// trail is append-only because a database trigger makes it so — the entity doc comment, the
/// delivery model builder, the context configuration (which justifies a CASCADE delete on the
/// grounds that "the header itself can never be deleted (append-only trigger)"), and the wiring
/// contract. <c>trg_master_data_audit_append_only</c> did not exist until 20260810110923.
///
/// <para>That is contract failure #7 — a control that reports success while doing nothing — and it
/// had reported success four times over. An audit trail whose rows can be updated is not evidence
/// of anything: the whole point of capturing a before/after on landed cost is that the person who
/// moved it cannot go back and change what the record says they moved it to.</para>
///
/// <para><c>PostgreSqlProductionDialectTests</c> now asserts the trigger exists, is enabled, and
/// fires BEFORE both UPDATE and DELETE on both tables. These tests assert the thing that actually
/// matters, which a catalogue check cannot: that it refuses. They live in the PostgreSQL lane
/// because the portable lane is SQLite and has no trigger — the same lane split that hid the
/// missing CHECK value this migration also repaired.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class MasterDataAuditAppendOnlyPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_audit_row_cannot_be_updated_or_deleted_even_by_the_table_owner()
    {
        var bu = Random.Shared.NextInt64(50_000_000, 60_000_000);
        await using var db = database.ContextFor(null);
        Seed.BusinessUnit(db, bu);
        await db.SaveChangesAsync();

        var change = new MasterDataChangeEvent
        {
            BusinessUnitId = bu,
            EntityType = "Product",
            EntityId = 1,
            EntityLabel = "Append-only probe",
            ChangeType = "UPDATED",
            Actor = "tests",
            ChangeSource = "Test",
            FieldCount = 1,
            OccurredOn = DateTime.UtcNow
        };
        change.Fields.Add(new MasterDataFieldChange
        {
            BusinessUnitId = bu,
            FieldName = "FinalLandedCost",
            BeforeValue = "100.00",
            AfterValue = "250.00"
        });
        db.Add(change);
        await db.SaveChangesAsync();

        var fieldId = await db.Set<MasterDataFieldChange>()
            .Where(f => f.BusinessUnitId == bu).Select(f => f.Id).SingleAsync();

        // The connection this runs on belongs to the migration role, which OWNS these tables. Row
        // level security would not stop it and neither would a REVOKE — the owner is exactly who
        // an "the ledger has been tidied up" incident runs as. A trigger binds the owner too, and
        // that is why the REVOKE in the same migration is not sufficient on its own.
        var updateHeader = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync($"""
                UPDATE public."MasterDataChangeEvents" SET "Reason" = 'rewritten' WHERE "BusinessUnitId" = {bu}
                """));
        Assert.Equal("55000", updateHeader.SqlState);
        Assert.Contains("append-only", updateHeader.MessageText);

        var updateField = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync($"""
                UPDATE public."MasterDataFieldChanges" SET "AfterValue" = '100.00' WHERE "Id" = {fieldId}
                """));
        Assert.Equal("55000", updateField.SqlState);

        var deleteField = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync($"""
                DELETE FROM public."MasterDataFieldChanges" WHERE "Id" = {fieldId}
                """));
        Assert.Equal("55000", deleteField.SqlState);

        // Deleting the header is refused before the CASCADE to its field rows can run, which is
        // the assumption ErpRfqAutomationContext.MasterDataAudit states in so many words when it
        // configures that cascade as describing an unreachable state.
        var deleteHeader = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync($"""
                DELETE FROM public."MasterDataChangeEvents" WHERE "BusinessUnitId" = {bu}
                """));
        Assert.Equal("55000", deleteHeader.SqlState);

        // The before/after that a landed-cost dispute would be settled on is still what was written.
        Assert.Equal("250.00", await db.Set<MasterDataFieldChange>().AsNoTracking()
            .Where(f => f.Id == fieldId).Select(f => f.AfterValue).SingleAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_refused_ai_extraction_can_record_its_outcome_state()
    {
        // IngestionOutcomeState.AI_NOT_AUTHORIZED is written by ExtractionWorker when a tenant's
        // policy refuses external AI processing. Until 20260810110923 the CHECK constraint on
        // source_document_occurrences.outcome_state did not admit it, and both lanes were green:
        // the portable lane runs SQLite with PRAGMA ignore_check_constraints = ON, and the model
        // agreed with the migrations, so nothing disagreed with anything. The first tenant to
        // upload a document with AI disabled would have taken a 23514 in production and nowhere
        // else. The entity's own doc comment had stated the rule — deploy the constraint before
        // the code that writes the value — which is the order this finally follows.
        //
        // This EVALUATES the constraint as actually deployed rather than matching a string against
        // it: the live definition is read out of pg_constraint, applied to a scratch table, and the
        // value is inserted. A LIKE '%AI_NOT_AUTHORIZED%' would also pass if the name turned up in
        // a comment or a neighbouring column's constraint. If the constraint refuses the value the
        // INSERT raises 23514 and this test fails with the same error production would have taken.
        await using var db = database.ContextFor(null);
        await db.Database.ExecuteSqlRawAsync("""
            DO $probe$
            DECLARE constraint_definition text;
            BEGIN
                SELECT pg_get_constraintdef(oid) INTO constraint_definition
                FROM pg_constraint
                WHERE conname = 'ck_source_document_occurrences_outcome_state';

                IF constraint_definition IS NULL THEN
                    RAISE EXCEPTION 'ck_source_document_occurrences_outcome_state does not exist';
                END IF;

                CREATE TEMP TABLE outcome_state_probe (outcome_state varchar(48)) ON COMMIT DROP;
                EXECUTE format(
                    'ALTER TABLE outcome_state_probe ADD CONSTRAINT probe %s', constraint_definition);
                INSERT INTO outcome_state_probe VALUES ('AI_NOT_AUTHORIZED');
            END
            $probe$;
            """);
    }
}
