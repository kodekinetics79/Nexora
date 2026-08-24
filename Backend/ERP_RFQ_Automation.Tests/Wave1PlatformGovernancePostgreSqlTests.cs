using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Wave1PlatformGovernancePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Wave1_transactions_execute_with_production_retry_strategy()
    {
        const long tenantId = 60_970;
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantId);
            await seed.SaveChangesAsync();
        }

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(tenantId));
        var artifacts = new PlatformGovernanceService(context);
        var suite = await artifacts.CreateAsync(tenantId, 81, "retry-suite-create",
            new(GovernedArtifactType.TestSuite, "retry-suite", "Retry suite", "Production retry regression",
                "{\"requirements\":[],\"tests\":[{\"name\":\"atomic\",\"actual\":true,\"expected\":true}],\"environment\":\"Sandbox\",\"passThreshold\":1}",
                "Initial retry-safe suite"), default);
        Assert.Equal(GovernedLifecycleStatus.Draft, suite.Artifact.Status);

        var actions = new HumanActionService(context);
        var action = await actions.CreateAsync(tenantId, 81, "retry-action-create",
            new("REVIEW", "Lead", "RETRY-001", "Retry review", "Review required",
                "Approve after evidence review", "{\"verified\":true}", 0.8m,
                "Prevents incorrect routing", "RESUME", HumanActionPriority.High, null,
                DateTime.UtcNow.AddHours(1)), default);
        var completed = await actions.TransitionAsync(tenantId, action.Item.Id, 81,
            "retry-action-complete", new(action.Item.Version, HumanActionStatus.Completed,
                "APPROVE", "Verified under production retry strategy"), default);
        Assert.Equal(HumanActionStatus.Completed, completed.Item.Status);

        var corpus = DocumentCorpus.Create(tenantId, Guid.NewGuid(), CorpusSourceType.Api);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var document = SourceDocument.Create(tenantId, corpus.Id, new string('c', 64),
            "retry-archive.pdf", "application/pdf", "evidence", "retry/archive.pdf", "v1", 128);
        context.Add(document);
        await context.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(tenantId, document.Id, corpus.Id,
            "retry-archive-occurrence", "{\"source\":\"test\"}");
        context.Add(occurrence);
        await context.SaveChangesAsync();
        var governed = await new CommercialDocumentArchiveService(context).GovernAsync(
            tenantId, 81, occurrence.Id, "retry-archive-hold",
            new(0, "HOLD_APPLIED", "Retry strategy regression"), default);
        Assert.True(governed.LegalHold);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Quality_analytics_reconciles_tenant_records_and_discloses_missing_accuracy_evidence()
    {
        const long tenantA = 60_971;
        const long tenantB = 60_972;
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantA);
            Seed.EnsureBusinessUnit(seed, tenantB);
            await seed.SaveChangesAsync();
        }
        await using (var context = database.ContextFor(tenantA))
        {
            var corpus = DocumentCorpus.Create(tenantA, Guid.NewGuid(), CorpusSourceType.Api);
            context.Add(corpus);
            await context.SaveChangesAsync();
            var document = SourceDocument.Create(tenantA, corpus.Id, new string('b', 64),
                "quality-rfq.pdf", "application/pdf", "evidence", "tenant-a/quality-rfq.pdf", "v1", 1024);
            context.Add(document);
            await context.SaveChangesAsync();
            context.Add(SourceDocumentOccurrence.Create(tenantA, document.Id, corpus.Id,
                "quality-occurrence", "{\"source\":\"test\"}"));
            await context.SaveChangesAsync();

            var view = await new QualityAnalyticsService(context).GetAsync(tenantA, 30, null, default);
            var reuse = Assert.Single(view.Metrics, x => x.Key == "correction-reuse");
            var fieldAccuracy = Assert.Single(view.Metrics, x => x.Key == "field-validation");
            Assert.Equal(1, reuse.Denominator);
            Assert.Equal(0m, reuse.Value);
            Assert.Null(fieldAccuracy.Value);
            Assert.Equal("InsufficientEvidence", fieldAccuracy.EvidenceStatus);

            // The disclosure must make two claims, not one phrase. Asserting the substance
            // rather than the wording: none of these rates is an accuracy, and a real one
            // needs labelled ground truth.
            Assert.Contains("none should be quoted as one", view.AccuracyLimitation);
            Assert.Contains("labelled ground truth", view.AccuracyLimitation);

            // Sample size toward a publishable figure — a COUNT with no value, so it can
            // never be read as an accuracy by sitting next to the percentages above.
            var corpusProgress = Assert.Single(view.Metrics, x => x.Key == "accuracy-corpus");
            Assert.Null(corpusProgress.Value);
            Assert.Equal("documents", corpusProgress.Unit);
            Assert.Equal(0, corpusProgress.Numerator);
            Assert.Equal("InsufficientEvidence", corpusProgress.EvidenceStatus);
        }
        await using var tenantBContext = database.ContextFor(tenantB);
        var tenantBView = await new QualityAnalyticsService(tenantBContext).GetAsync(tenantB, 30, null, default);
        Assert.DoesNotContain(tenantBView.Records, x => x.FileName == "quality-rfq.pdf");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Archive_search_and_legal_hold_are_tenant_safe_audited_and_idempotent()
    {
        const long tenantA = 60_981;
        const long tenantB = 60_982;
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantA);
            Seed.EnsureBusinessUnit(seed, tenantB);
            await seed.SaveChangesAsync();
        }
        long occurrenceId;
        await using (var context = database.ContextFor(tenantA))
        {
            var corpus = DocumentCorpus.Create(tenantA, Guid.NewGuid(), CorpusSourceType.ManualUpload);
            context.Add(corpus);
            await context.SaveChangesAsync();
            var document = SourceDocument.Create(tenantA, corpus.Id, new string('a', 64),
                "customer-rfq-archive.pdf", "application/pdf", "evidence", "tenant-a/archive.pdf",
                "v1", 4096);
            context.Add(document);
            await context.SaveChangesAsync();
            var occurrence = SourceDocumentOccurrence.Create(tenantA, document.Id, corpus.Id,
                "archive-occurrence", "{\"source\":\"test\"}");
            context.Add(occurrence);
            await context.SaveChangesAsync();
            occurrenceId = occurrence.Id;

            var service = new CommercialDocumentArchiveService(context);
            var result = await service.SearchAsync(tenantA,
                new("customer-rfq", null, "Accepted", null, null, "newest"), default);
            Assert.Contains(result.Items, x => x.OccurrenceId == occurrenceId
                && x.ContentHash == new string('a', 64));
            var hold = await service.GovernAsync(tenantA, 91, occurrenceId, "archive-hold",
                new(0, "HOLD_APPLIED", "Legal review requested"), default);
            var replay = await service.GovernAsync(tenantA, 91, occurrenceId, "archive-hold",
                new(0, "HOLD_APPLIED", "Legal review requested"), default);
            Assert.True(hold.LegalHold);
            Assert.True(replay.IdempotentReplay);
            // DELETION_REQUESTED no longer exists as an action. It never had an approver, so
            // every request was permanent — and the retention purge read the resulting flag as an
            // EXCLUSION, which made "request deletion review" the one reliable way to stop a
            // document ever being deleted. The archive now refuses to record it at all.
            var refused = await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
                service.GovernAsync(tenantA, 91, occurrenceId, "archive-delete",
                    new(hold.GovernanceVersion, "DELETION_REQUESTED", "Retention review"), default));
            Assert.Contains("Storage & Retention", refused.Message);
            Assert.DoesNotContain("DELETION", refused.Message, StringComparison.Ordinal);
        }
        await using var tenantBContext = database.ContextFor(tenantB);
        var tenantBResult = await new CommercialDocumentArchiveService(tenantBContext).SearchAsync(tenantB,
            new(null, null, null, null, null, "newest"), default);
        Assert.DoesNotContain(tenantBResult.Items, x => x.OccurrenceId == occurrenceId);
        Assert.Empty(await tenantBContext.TenantGovernanceAuditEvents
            .Where(x => x.AggregateReference == $"occurrence:{occurrenceId}").ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Wave1_schema_is_forced_rls_least_privilege_and_event_ledgers_are_append_only()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        // Squash note: dropped the leading id check for '20260730044854_Wave1PlatformParity'.
        // 20260811033109_SquashedSchemaBaseline erased that id. Forced RLS on all six tables, the
        // six tenant policies, both grant shapes and the three append-only triggers are asserted
        // below, against pg_catalog.
        command.CommandText = """
            SELECT
                (SELECT bool_and(relrowsecurity AND relforcerowsecurity)
                    FROM pg_class WHERE oid = ANY(ARRAY[
                        'public.governed_artifacts'::regclass,
                        'public.governed_artifact_versions'::regclass,
                        'public.governed_artifact_events'::regclass,
                        'public.human_action_items'::regclass,
                        'public.human_action_events'::regclass,
                        'public.tenant_governance_audit_events'::regclass])),
                (SELECT count(*) = 6 FROM pg_policies
                    WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
                      AND tablename = ANY(ARRAY['governed_artifacts','governed_artifact_versions',
                        'governed_artifact_events','human_action_items','human_action_events',
                        'tenant_governance_audit_events'])),
                has_table_privilege('nexora_tenant_app', 'public.governed_artifacts', 'SELECT,INSERT,UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.governed_artifacts', 'DELETE,TRUNCATE'),
                has_table_privilege('nexora_tenant_app', 'public.governed_artifact_events', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.governed_artifact_events', 'UPDATE,DELETE,TRUNCATE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                    AND tgname = ANY(ARRAY['governed_artifact_events_append_only',
                        'human_action_events_append_only','tenant_governance_audit_events_append_only'])) = 3;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 5; index++)
            Assert.True(reader.GetBoolean(index), $"Wave 1 schema assertion {index + 1} failed.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Runtime_role_cannot_read_or_insert_another_tenants_artifact()
    {
        const long tenantA = 62_001;
        const long tenantB = 62_002;
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantA);
            Seed.EnsureBusinessUnit(seed, tenantB);
            await seed.SaveChangesAsync();
        }
        await using var connection = await database.OpenConnectionAsync();
        long artifactId;
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = InsertArtifactSql(tenantA, "tenant-a");
            artifactId = (long)(await insert.ExecuteScalarAsync())!;
        }
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.Transaction = transaction;
            scope.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{tenantB}';
                SELECT count(*) FROM governed_artifacts WHERE "Id" = {artifactId};
                """;
            Assert.Equal(0L, (long)(await scope.ExecuteScalarAsync())!);
        }
        await using (var forged = connection.CreateCommand())
        {
            forged.Transaction = transaction;
            forged.CommandText = InsertArtifactSql(tenantA, "forged");
            var error = await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        await transaction.RollbackAsync();
    }

    private static string InsertArtifactSql(long tenantId, string key) => $"""
        INSERT INTO governed_artifacts
            ("BusinessUnitId","ArtifactType","ArtifactKey","Name","Description","Status",
             "CurrentVersionNumber","ProductionVersionNumber","Version","CreatedOn",
             "CreatedByUserId","UpdatedOn","UpdatedByUserId")
        VALUES ({tenantId},'CommercialTaxonomy','{key}','Taxonomy','Test','Draft',1,NULL,1,
            now(),1,now(),1)
        RETURNING "Id";
        """;
}
