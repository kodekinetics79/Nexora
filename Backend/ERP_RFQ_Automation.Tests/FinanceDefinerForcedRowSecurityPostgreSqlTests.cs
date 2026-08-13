using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The governed finance journeys — post a journal entry, issue an invoice, release a refund, post a
/// write-off, reverse a receipt against a kept promise — against a database whose OWNER is bound by
/// its own row-level security.
///
/// <para><b>THE DEFECT.</b> On a <c>NOSUPERUSER NOBYPASSRLS NOINHERIT</c> schema owner — what
/// <c>Program.cs ValidateRuntimeDatabaseRoleAsync</c> demands and what every managed provider hands
/// out — not one of these journeys completed. SECURITY DEFINER switches the effective role to the
/// FUNCTION OWNER; 110 tables are <c>FORCE ROW LEVEL SECURITY</c>, which makes the owner subject to
/// its own policies; and every tenant policy is written <c>TO nexora_tenant_app</c>, a role list
/// PostgreSQL matches with <c>has_privs_of_role()</c>, which for a NOINHERIT owner does not include
/// roles it is merely a member of. So inside these functions the owner matched no policy on any
/// forced table.</para>
///
/// <para>Two symptoms, one mechanism. The WRITES were refused loudly with <c>42501</c> — the twelve
/// (SECURITY DEFINER function -> FORCE table) write pairs across ten functions that
/// 20260811210000 catalogued and left. The READS returned zero rows SILENTLY, so six of the eight
/// journeys never reached their write at all: their own evidence guard read nothing and raised
/// <c>23514</c> first. Both are asserted below, and the second is why this class drives whole
/// journeys rather than the twelve INSERT statements.</para>
///
/// <para><b>Why nothing in the ordinary PostgreSQL lane could catch it.</b>
/// <c>PostgreSqlTestDatabase</c> connects as the container's <c>POSTGRES_USER</c>, which
/// Testcontainers creates as a SUPERUSER and which also OWNS the schema, so the trigger bodies ran
/// as a superuser and row-level security was never evaluated for them at all. This class reuses
/// <see cref="ForcedRowSecurityOwnerDatabase"/>, which owns its own container because the execution
/// roles are cluster-scoped.</para>
///
/// <para><b>The fix under test</b> is
/// 20260812012000_DefinerTenantIsolationUnderForcedRowSecurity: three policies per forced tenant
/// table — SELECT, INSERT, UPDATE, never DELETE — granted <c>TO</c> the table's own owner and
/// admitting it only for the tenant named by <c>nexora.business_unit_id</c>, which is the same GUC
/// and the same predicate <c>nexora_tenant_isolation</c> already uses. So the assertions below are
/// as much about what the owner STILL cannot do as about what it now can.</para>
/// </summary>
public sealed class FinanceDefinerForcedRowSecurityPostgreSqlTests(ForcedRowSecurityOwnerDatabase database)
    : IClassFixture<ForcedRowSecurityOwnerDatabase>, IAsyncLifetime
{
    private const long Tenant = 91_001;

    /// <summary>The control. Every assertion about it is "invisible".</summary>
    private const long Neighbour = 91_002;

    private static int _fixtureSeeded;

    public async Task InitializeAsync()
    {
        // Seeded once for the class. Interlocked rather than a lazy field because xUnit constructs
        // one instance of this class per test and runs them against the single shared fixture.
        if (Interlocked.Exchange(ref _fixtureSeeded, 1) == 1)
            return;
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------------------- the journeys

    /// <summary>
    /// Creating an accounting book. The shortest journey that reaches the defect in its raw form:
    /// <c>nexora_gl_evidence_event</c> reads nothing at all, so before the fix this failed with the
    /// bare <c>42501</c> rather than with a guard's complaint.
    ///
    /// <para>Both evidence tables are asserted. <c>nexora_gl_evidence_event</c> writes
    /// <c>CommercialFinanceAudits</c> AND <c>FinanceOutboxMessages</c> in that order, so a fix that
    /// covered only the first would leave the journey failing one statement later.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Creating_an_accounting_book_writes_its_finance_evidence()
    {
        await AssertTheOwnerIsBoundByRowLevelSecurityAsync();
        await CreateLedgerBookAsync();

        // Counted through a SUPERUSER connection, deliberately. Asking the identity that did the
        // write whether the write happened is the failure mode this whole class exists to close.
        Assert.Equal(1L, await CountAsync(
            "public.\"CommercialFinanceAudits\"", $"\"AggregateType\" = 'LedgerBook' AND \"BusinessUnitId\" = {Tenant}"));
        Assert.Equal(1L, await CountAsync(
            "public.\"FinanceOutboxMessages\"", $"\"EventType\" = 'finance.ledgerbook.created' AND \"BusinessUnitId\" = {Tenant}"));
    }

    /// <summary>
    /// Posting a journal entry: the journey the general ledger exists for, and the one that
    /// allocates a legal document number.
    ///
    /// <para>It exercises three of the ten functions in one transition.
    /// <c>nexora_gl_guard_journal</c> upserts <c>public."LegalDocumentCounters"</c> —
    /// <c>INSERT ... ON CONFLICT DO UPDATE ... RETURNING</c>, which needs the SELECT policy for the
    /// arbiter, the INSERT policy for the first allocation and the UPDATE policy for every one
    /// after, so a fix with any one of the three missing fails here. <c>nexora_gl_validate_posting</c>
    /// and the guard both READ <c>JournalEntryLines</c>, <c>LedgerAccounts</c>,
    /// <c>AccountingPeriods</c> and <c>Currency</c>, all forced, which is why before the fix this
    /// journey died at "a tenant accounting book is required" without ever reaching a write.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Posting_a_journal_entry_allocates_its_legal_number_and_evidence()
    {
        await CreateLedgerBookAsync();

        await AsTenantAsync($"""
            INSERT INTO public."JournalEntries" ("Id","BusinessUnitId","AccountingPeriodId","FunctionalCurrencyId","AccountingDate","Status","Description","SourceType","TotalDebit","TotalCredit","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},91001,91001,'2026-08-10','Draft','Definer journal','Manual',1000,1000,'idem-jrn-1',repeat('j',64),1,'finance-definer-tests',now());
            INSERT INTO public."JournalEntryLines" ("Id","BusinessUnitId","JournalEntryId","Sequence","LedgerAccountId","Description","TransactionCurrencyId","ExchangeRate","TransactionDebit","TransactionCredit","FunctionalDebit","FunctionalCredit")
            VALUES (91001,{Tenant},91001,1,91003,'Debit cash',91001,1,1000,0,1000,0),
                   (91002,{Tenant},91001,2,91002,'Credit revenue',91001,1,0,1000,0,1000);
            SELECT public.nexora_test_actor_envelope({Tenant},'finance-definer-controller');
            UPDATE public."JournalEntries" SET "Status"='Posted', "PostedBy"='finance-definer-controller', "PostedOn"=now(), "Version"=2 WHERE "Id"=91001;
            """);

        // The number is the point of the counter, not a detail of it: a posted journal with no
        // legal sequence is not a posted journal in any jurisdiction this product ships to.
        Assert.Equal("JRN-2026-00000001", await StringAsync($"""
            SELECT "EntryNumber" FROM public."JournalEntries" WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """));
        Assert.Equal(1L, await CountAsync("public.\"LegalDocumentCounters\"",
            $"\"BusinessUnitId\" = {Tenant} AND \"DocumentType\" = 'Journal' AND \"NextNumber\" = 2"));
        Assert.Equal(1L, await CountAsync("public.\"CommercialFinanceAudits\"",
            $"\"BusinessUnitId\" = {Tenant} AND \"AggregateType\" = 'JournalEntry' AND \"Action\" = 'Posted'"));
    }

    /// <summary>
    /// Issuing an invoice. <c>nexora_receivable_issued_immutable</c> reconciles the header against
    /// <c>ReceivableDocumentLines</c> and the source order before it allocates anything, then
    /// allocates from <c>LegalDocumentCounters</c> and calls
    /// <c>nexora_write_finance_audit</c> — which is itself one of the ten, and which READS
    /// <c>ReceivableDocuments</c> to check that the action it is being asked to record matches the
    /// document's real state. Before the fix the reconciliation saw zero lines and raised
    /// "receivable lines and header do not reconcile" against a document whose lines were right
    /// there.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Issuing_an_invoice_allocates_its_legal_number_and_audit()
    {
        await AsTenantAsync($"""
            UPDATE public."ReceivableDocuments"
               SET "Status"='Issued', "IssuedBy"='finance-definer-checker', "IssuedOn"=now(), "Version"=2
             WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """);

        Assert.Equal("INV-2026-000001", await StringAsync($"""
            SELECT "DocumentNumber" FROM public."ReceivableDocuments" WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """));
        Assert.Equal(1L, await CountAsync("public.\"CommercialFinanceAudits\"",
            $"\"BusinessUnitId\" = {Tenant} AND \"AggregateType\" = 'ReceivableDocument' AND \"Action\" = 'Issued'"));
    }

    /// <summary>
    /// Reversing a receipt that a customer's kept promise to pay was matched against.
    ///
    /// <para>This is the only one of the twelve write pairs that is an UPDATE of an existing tenant
    /// row rather than an append: <c>nexora_ar_reconcile_kept_promise_payment</c> re-opens the
    /// promise as Broken. It needs the SELECT policy to find the promise and the UPDATE policy to
    /// change it, and it is the reason the fix could not have been INSERT-only. The governance
    /// consequence if it silently does nothing is a promise that stays "Kept" against money the
    /// bank took back — a collections team standing down on a debt that is still owed.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Reversing_a_receipt_breaks_the_promise_it_was_matched_to()
    {
        await AsTenantAsync($"""
            UPDATE public."CustomerPayments"
               SET "Status"='Reversed', "ReversedBy"='finance-definer-controller', "ReversedOn"=now(),
                   "ReversalReason"='Bank returned the transfer unpaid', "Version"=2
             WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91002;
            """);

        Assert.Equal("Broken", await StringAsync($"""
            SELECT "Status" FROM public."PromisesToPay" WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """));
        // The match is cleared too, not just the label. A Broken promise still pointing at the
        // reversed receipt would re-match on the next reconciliation sweep.
        Assert.Equal(1L, await CountAsync("public.\"PromisesToPay\"",
            $"\"BusinessUnitId\" = {Tenant} AND \"Id\" = 91001 AND \"MatchedPaymentId\" IS NULL AND \"MatchedAmount\" IS NULL"));
    }

    /// <summary>
    /// Releasing a refund and posting a write-off, the two remaining legal-counter allocators.
    /// Asserted together because they are the same shape as each other — a governed status
    /// transition whose guard reads its own allocations or its source receipt first, then allocates
    /// a number — and because each proves a different <c>DocumentType</c> partition of the counter
    /// upsert takes the INSERT branch rather than colliding with the other's row.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Releasing_a_refund_and_posting_a_write_off_allocate_their_numbers()
    {
        await AsTenantAsync($"""
            UPDATE public."CustomerRefunds"
               SET "Status"='Released', "ReleasedBy"='finance-definer-releaser', "ReleasedOn"=now(),
                   "RefundNumber"='PENDING', "PostingStatus"='PendingDisbursement', "Version"=3
             WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """);
        await AsTenantAsync($"""
            UPDATE public."ReceivableWriteOffs"
               SET "Status"='Posted', "ApprovedBy"='finance-definer-approver', "ApprovedOn"=now(),
                   "PostingStatus"='PendingExport', "WriteOffNumber"='PENDING', "Version"=2
             WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """);

        Assert.Equal("RFD-2026-000001", await StringAsync($"""
            SELECT "RefundNumber" FROM public."CustomerRefunds" WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """));
        Assert.Equal("WOF-2026-000001", await StringAsync($"""
            SELECT "WriteOffNumber" FROM public."ReceivableWriteOffs" WHERE "BusinessUnitId" = {Tenant} AND "Id" = 91001;
            """));
    }

    // ------------------------------------------------------------- what the owner still cannot do

    /// <summary>
    /// FORCE is still real for the owner, which is the half of the fix that is easy to lose.
    ///
    /// <para>The policies admit the owner for ONE tenant, named by the GUC the request already
    /// declares. With no GUC set the owner reads nothing at all — not the evidence it just wrote,
    /// not any other tenant's. A fix that had simply admitted the owner, turned FORCE off, or
    /// handed something BYPASSRLS fails here rather than in some later audit.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_owner_reads_nothing_without_a_tenant_scope_and_one_tenant_with_it()
    {
        await CreateLedgerBookAsync();

        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();

        Assert.Equal(0L, await ScalarOnAsync(connection,
            """SELECT count(*)::bigint FROM public."CommercialFinanceAudits";"""));
        Assert.Equal(0L, await ScalarOnAsync(connection,
            """SELECT count(*)::bigint FROM public."LedgerBooks";"""));

        // Pointed at the neighbouring tenant, the owner still sees nothing of this one's. The
        // neighbour is a real business unit, so this is a scope test and not a "no such tenant" one.
        await ExecuteOnAsync(connection, $"SELECT set_config('nexora.business_unit_id','{Neighbour}',false);");
        Assert.Equal(0L, await ScalarOnAsync(connection,
            """SELECT count(*)::bigint FROM public."CommercialFinanceAudits";"""));

        await ExecuteOnAsync(connection, $"SELECT set_config('nexora.business_unit_id','{Tenant}',false);");
        Assert.Equal(1L, await ScalarOnAsync(connection,
            $"""SELECT count(DISTINCT "BusinessUnitId")::bigint FROM public."CommercialFinanceAudits";"""));
        Assert.Equal(0L, await ScalarOnAsync(connection,
            $"""SELECT count(*)::bigint FROM public."CommercialFinanceAudits" WHERE "BusinessUnitId" <> {Tenant};"""));
    }

    /// <summary>
    /// The tenant role gained nothing. PostgreSQL ORs permissive policies, and a new permissive
    /// policy that a request identity can match is exactly the shape that produced the cross-tenant
    /// INSERT 20260811110019 removed from <c>AiProcessingPolicies</c>. These three cannot be matched
    /// on a request path for two independent reasons — the role list names the schema owner, and the
    /// predicate leads with <c>current_user = &lt;the table's owner&gt;</c> — but "cannot" is the
    /// claim, so it is asserted rather than argued: the tenant role still cannot write its own audit
    /// evidence, and still cannot see a neighbour's.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_tenant_role_still_cannot_write_its_own_evidence_or_read_a_neighbour()
    {
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => AsTenantAsync($"""
            INSERT INTO public."CommercialFinanceAudits"
                ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
            VALUES ({Tenant},'ReceivableDocument',91001,'Issued','forged',now(),jsonb_build_object());
            """));
        Assert.Equal("42501", refusal.SqlState);

        // And the same statement aimed at the neighbour is refused for the same reason rather than
        // for a different one, so the assertion above is not passing on the tenant filter alone.
        var crossTenant = await Assert.ThrowsAsync<PostgresException>(() => AsTenantAsync($"""
            INSERT INTO public."CommercialFinanceAudits"
                ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
            VALUES ({Neighbour},'ReceivableDocument',91001,'Issued','forged',now(),jsonb_build_object());
            """));
        Assert.Equal("42501", crossTenant.SqlState);

        await CreateLedgerBookAsync();
        Assert.Equal(0L, await TenantScopedScalarAsync(Neighbour,
            """SELECT count(*)::bigint FROM public."CommercialFinanceAudits";"""));
    }

    /// <summary>
    /// The defect itself, driven rather than described: with the three policies dropped, the ledger
    /// book journey answers the original <c>42501</c> from inside
    /// <c>nexora_gl_evidence_event</c>.
    ///
    /// <para>This is what makes the class above meaningful. Every other test here would also pass on
    /// a superuser-owned database, where row-level security is never evaluated and the fix is
    /// invisible; this one fails unless the policies are both present and load-bearing. The drop and
    /// the journey share one transaction that is ROLLED BACK — DDL is transactional in PostgreSQL,
    /// so the policies come back — and <c>SET CONSTRAINTS ALL IMMEDIATE</c> is what makes the
    /// deferred evidence trigger fire inside it instead of at a commit that never happens.</para>
    ///
    /// <para>The journey is a dunning policy rather than the ledger book the first test uses,
    /// because <c>UX_LedgerBooks_BU</c> permits exactly one accounting book per tenant: whichever of
    /// the two ran first would leave the other hitting a <c>23505</c> before the trigger it is
    /// trying to provoke ever fired, and this test would then pass or fail on xUnit's ordering
    /// rather than on the policies. <c>nexora_ar_evidence_event</c> reaches the same
    /// <c>CommercialFinanceAudits</c> INSERT and has no such uniqueness.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Dropping_the_definer_policies_brings_the_refusal_straight_back()
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteOnAsync(connection, """
            DROP POLICY nexora_definer_tenant_read   ON public."CommercialFinanceAudits";
            DROP POLICY nexora_definer_tenant_insert ON public."CommercialFinanceAudits";
            DROP POLICY nexora_definer_tenant_update ON public."CommercialFinanceAudits";
            """);

        var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOnAsync(connection, $"""
            SET CONSTRAINTS ALL IMMEDIATE;
            SET LOCAL ROLE nexora_tenant_app;
            SELECT set_config('nexora.business_unit_id','{Tenant}',true);
            SELECT public.nexora_test_actor_envelope({Tenant},'finance-definer-tests');
            INSERT INTO public."DunningPolicies"
                ("Id","BusinessUnitId","PolicyVersion","Name","Status","JurisdictionCode","TimeZoneId","GraceDays",
                 "CadenceDays","MaximumStage","MinimumOverdueAmount","QuietHoursStart","QuietHoursEnd","TemplateVersion",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91009,{Tenant},9,'Red policy','Draft','SA','Asia/Riyadh',5,7,3,100,21,7,'t1',
                    'idem-policy-red',repeat('r',64),1,'finance-definer-tests',now());
            """));

        // Matched on the message rather than on PostgresException.TableName, which PostgreSQL leaves
        // unset for a row-level-security refusal — the check that failed is a policy, not a
        // constraint on a relation, so there is no relation field to populate. Asserting both the
        // SQLSTATE and the wording keeps this from passing on some other 42501, of which there are
        // several available on this path (the tenant role has no INSERT here at all).
        Assert.Equal("42501", refusal.SqlState);
        Assert.Equal(
            "new row violates row-level security policy for table \"CommercialFinanceAudits\"",
            refusal.MessageText);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The request-reachability invariant that <c>AiProcessingPolicyTenantIsolationPostgreSqlTests</c>
    /// and <c>PostgreSqlProductionDialectTests</c> enforce, re-run here against the forced-owner
    /// database and then deliberately attacked.
    ///
    /// <para><b>Why this test exists.</b> Those two guards say "exactly one permissive policy on
    /// <c>public."AiProcessingPolicies"</c> that any request identity can match", and they exist
    /// because a cross-tenant INSERT hole was closed there and then reopened by a well-intentioned
    /// edit that passed every assertion of the day. This migration adds three permissive policies to
    /// that table among ninety-nine others. It does NOT amend either guard, and it does not ask for
    /// an exemption: the policies are granted <c>TO</c> the schema owner, and none of the three
    /// request roles is a member of it — the GRANT in <c>Sql/00_execution_roles.sql</c> runs the
    /// other way — so they fall out of the guard's own
    /// <c>pg_has_role(request_role, admitted, 'USAGE')</c> test, which is the same function
    /// PostgreSQL uses to match a policy's role list. The first assertion is that claim.</para>
    ///
    /// <para><b>Why the two hostile policies.</b> Showing that the guard passes proves only that it
    /// is quiet, and a guard that has been accidentally defanged is also quiet. So the same query is
    /// run against two policies designed to defeat it: one openly <c>TO PUBLIC</c> with no fence at
    /// all, and one carrying this migration's exact owner fence in a DEAD DISJUNCT, which is the
    /// shape that beat an earlier substring-based version of this invariant while admitting every
    /// cross-tenant row. Both must be named. Both are created and dropped inside a transaction that
    /// is rolled back, so the fixture is unchanged either way.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_definer_policies_are_unreachable_by_any_request_identity_and_the_guard_still_bites()
    {
        // Character-for-character the reachability predicate both guards use.
        const string GuardQuery = """
            SELECT coalesce(string_agg(policy.polname, ', ' ORDER BY policy.polname), '<none>')
            FROM pg_policy policy
            JOIN pg_class target ON target.oid = policy.polrelid
            JOIN pg_namespace target_schema
              ON target_schema.oid = target.relnamespace AND target_schema.nspname = 'public'
            WHERE target.relname = 'AiProcessingPolicies'
              AND policy.polpermissive
              AND (policy.polroles = '{0}'::oid[]
                   OR EXISTS (
                       SELECT 1
                       FROM unnest(policy.polroles) AS admitted(role_oid)
                       CROSS JOIN pg_roles request_role
                       WHERE request_role.rolname IN (
                                 'nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
                         AND pg_has_role(request_role.oid, admitted.role_oid, 'USAGE')))
              AND policy.polname NOT IN ('nexora_ai_default_policy_seed_read',
                                         'nexora_ai_default_policy_seed_write');
            """;

        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();

        // The three definer policies really are on this table, so the pass below is not vacuous.
        Assert.Equal(3L, await ScalarOnAsync(connection, """
            SELECT count(*)::bigint FROM pg_policy policy
            JOIN pg_class target ON target.oid = policy.polrelid
            WHERE target.relname = 'AiProcessingPolicies'
              AND policy.polname LIKE 'nexora\_definer\_tenant\_%';
            """));
        Assert.Equal("nexora_tenant_isolation", await StringOnAsync(connection, GuardQuery));

        await using var transaction = await connection.BeginTransactionAsync();

        await ExecuteOnAsync(connection, """
            CREATE POLICY nexora_hostile_unfenced ON public."AiProcessingPolicies"
                AS PERMISSIVE FOR INSERT TO PUBLIC WITH CHECK (true);
            """);
        Assert.Equal("nexora_hostile_unfenced, nexora_tenant_isolation",
            await StringOnAsync(connection, GuardQuery));

        await ExecuteOnAsync(connection, """
            CREATE POLICY nexora_hostile_dead_disjunct ON public."AiProcessingPolicies"
                AS PERMISSIVE FOR INSERT TO PUBLIC
                WITH CHECK ("UpdatedBy" = 'x'
                            OR current_user = (SELECT pg_catalog.pg_get_userbyid(t.relowner)
                                               FROM pg_catalog.pg_class t
                                               WHERE t.oid = 'public."AiProcessingPolicies"'::regclass));
            """);
        Assert.Equal(
            "nexora_hostile_dead_disjunct, nexora_hostile_unfenced, nexora_tenant_isolation",
            await StringOnAsync(connection, GuardQuery));

        await transaction.RollbackAsync();
        Assert.Equal("nexora_tenant_isolation", await StringOnAsync(connection, GuardQuery));
    }

    // ------------------------------------------------------------------------------- assertions

    /// <summary>
    /// Proves the fixture reproduces the production shape before any test leans on it. If these
    /// drift, every assertion above becomes vacuous rather than false — which is the same class of
    /// silence the defect itself was.
    /// </summary>
    private async Task AssertTheOwnerIsBoundByRowLevelSecurityAsync()
    {
        Assert.Equal(1L, await CountAsync("pg_roles",
            $"rolname = '{ForcedRowSecurityOwnerDatabase.OwnerRole}' AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit"));

        // All four write targets are still FORCE and still owned by that role. A fix that reached
        // for `ALTER TABLE ... NO FORCE` fails right here.
        Assert.Equal(4L, await CountAsync("pg_class c",
            $"""
             c.oid IN ('public."CommercialFinanceAudits"'::regclass, 'public."FinanceOutboxMessages"'::regclass,
                       'public."LegalDocumentCounters"'::regclass, 'public."PromisesToPay"'::regclass)
               AND c.relrowsecurity AND c.relforcerowsecurity
               AND pg_get_userbyid(c.relowner) = '{ForcedRowSecurityOwnerDatabase.OwnerRole}'
             """));

        // And all ten writers are still SECURITY DEFINER and still owned by that role — the two
        // facts that make the effective role for their statements the owner rather than the caller.
        Assert.Equal(10L, await CountAsync("pg_proc p",
            $"""
             p.prosecdef AND pg_get_userbyid(p.proowner) = '{ForcedRowSecurityOwnerDatabase.OwnerRole}'
               AND p.oid IN ('public.nexora_ar_evidence_event()'::regprocedure,
                             'public.nexora_ar_reconcile_kept_promise_payment()'::regprocedure,
                             'public.nexora_bank_evidence_event()'::regprocedure,
                             'public.nexora_gl_evidence_event()'::regprocedure,
                             'public.nexora_gl_guard_journal()'::regprocedure,
                             'public.nexora_receivable_issued_immutable()'::regprocedure,
                             'public.nexora_refund_governed()'::regprocedure,
                             'public.nexora_write_off_governed()'::regprocedure,
                             'public.nexora_write_finance_audit(bigint,text,bigint,text,text,jsonb,timestamp without time zone)'::regprocedure,
                             'public.nexora_write_finance_outbox(bigint,text,bigint,bigint,text,jsonb,timestamp without time zone)'::regprocedure)
             """));

        // The owner does not inherit the role its own tenant policies name. This single fact is why
        // nexora_tenant_isolation could never have admitted these functions.
        Assert.Equal(0L, await CountAsync("(SELECT 1) probe(x)",
            $"pg_has_role('{ForcedRowSecurityOwnerDatabase.OwnerRole}', 'nexora_tenant_app', 'USAGE')"));

        // Nothing acquired BYPASSRLS on the way to the fix. The pattern is 'nexora\_%' rather than
        // 'nexora%' on purpose: the container's own POSTGRES_USER is called `nexora` and is a
        // superuser, so the looser pattern would fold the harness's login into an assertion about
        // the application's roles.
        Assert.Equal(2L, await CountAsync("pg_roles", @"rolbypassrls AND rolname LIKE 'nexora\_%'"));

        // The fix is additive: nexora_tenant_isolation is still on every table it was on, and the
        // new policies never replace it.
        //
        // 220 -> 221 with Gate 2's SupplierComparisonWeights. A census like this is meant to move
        // when a tenant-owned table is legitimately added, and moving it is the point: had the Gate 2
        // migration created that table WITHOUT its policy, this number would have stayed at 220 and
        // said so. It going up by exactly one is the evidence the policy reached real PostgreSQL,
        // which no portable test can establish.
        //
        // 221 -> 223 with EmailInquiryAssemblies and EmailInquiryComponents. Exactly two, for the
        // two tenant-owned tables the email inquiry assembly adds — the census doing its job again.
        Assert.Equal(223L, await CountAsync("pg_policy", "polname = 'nexora_tenant_isolation'"));
        Assert.Equal(300L, await CountAsync("pg_policy",
            "polname IN ('nexora_definer_tenant_read','nexora_definer_tenant_insert','nexora_definer_tenant_update')"));

        // DELETE is not among them, on any table. Several of the ten writers exist to make rows
        // immutable, and a FOR ALL policy would have handed the identity they run as a latent
        // tenant-wide DELETE. polcmd: 'r' SELECT, 'a' INSERT, 'w' UPDATE, 'd' DELETE, '*' ALL.
        Assert.Equal(0L, await CountAsync("pg_policy",
            "polname LIKE 'nexora\\_definer\\_tenant\\_%' AND polcmd IN ('d', '*')"));

        // Not one of the three hundred is TO PUBLIC, and every one names the CURRENT owner of the
        // table it is on. The first half is what keeps them out of the request-reachability
        // invariant; the second is what stops the migration's repair branch from being the only
        // thing standing between an ownership change and a fix that has silently stopped working.
        Assert.Equal(0L, await CountAsync("pg_policy",
            "polname LIKE 'nexora\\_definer\\_tenant\\_%' AND polroles = '{0}'::oid[]"));
        Assert.Equal(0L, await CountAsync("pg_policy p JOIN pg_class c ON c.oid = p.polrelid",
            "p.polname LIKE 'nexora\\_definer\\_tenant\\_%' AND p.polroles IS DISTINCT FROM ARRAY[c.relowner]"));
    }

    // ---------------------------------------------------------------------------------- fixture

    /// <summary>
    /// The tenant's finance spine, seeded through the SUPERUSER connection with
    /// <c>session_replication_role = replica</c> so that no SECURITY DEFINER trigger fires while it
    /// is built. That is not a way of dodging the defect: the journeys above are what must fire
    /// those triggers, and a fixture that fired them too would be asserting the fix from inside its
    /// own setup.
    /// </summary>
    private async Task SeedAsync()
    {
        // A stand-in for the signed actor envelope the application computes in C# and pushes through
        // TenantRlsCommandInterceptor. SECURITY DEFINER and owned by the superuser that creates it,
        // because "FinanceProviderSecrets" is deliberately unreadable to nexora_tenant_app.
        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.SuperuserConnectionString, """
            INSERT INTO public."FinanceProviderSecrets" ("Name","Secret","UpdatedOn")
            VALUES ('AuditActor', repeat('s',48), now()) ON CONFLICT ("Name") DO NOTHING;
            CREATE OR REPLACE FUNCTION public.nexora_test_actor_envelope(bu bigint, actor text)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER AS $envelope$
            DECLARE issued bigint := extract(epoch FROM clock_timestamp())::bigint;
            DECLARE expires bigint; DECLARE nonce uuid := gen_random_uuid(); DECLARE secret text;
            BEGIN
                expires := issued + 60;
                SELECT "Secret" INTO secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
                PERFORM set_config('nexora.actor_id', actor, true);
                PERFORM set_config('nexora.actor_signature', encode(hmac(convert_to(
                    bu::text || E'\n' || actor, 'UTF8'), convert_to(secret,'UTF8'), 'sha256'), 'hex'), true);
                PERFORM set_config('nexora.gl_issued_at', issued::text, true);
                PERFORM set_config('nexora.gl_expires_at', expires::text, true);
                PERFORM set_config('nexora.gl_nonce', nonce::text, true);
                PERFORM set_config('nexora.gl_signature', encode(hmac(convert_to(
                    bu::text || E'\n' || actor || E'\n' || issued::text || E'\n' || expires::text
                    || E'\n' || nonce::text, 'UTF8'), convert_to(secret,'UTF8'), 'sha256'), 'hex'), true);
            END $envelope$;
            GRANT EXECUTE ON FUNCTION public.nexora_test_actor_envelope(bigint,text) TO nexora_tenant_app;
            """);

        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.SuperuserConnectionString, $"""
            INSERT INTO public."BusinessUnits" ("ID","BusinessUnitCode","BusinessUnitName","CreatedBy")
            VALUES ({Tenant},'FINDEF','Finance definer tenant','finance-definer-tests'),
                   ({Neighbour},'FINNBR','Finance definer neighbour','finance-definer-tests');
            INSERT INTO public."Currency" ("ID","BusinessUnitID","Code","CurrencyName","IsBaseCurrency","IsActive","ExchangeRate","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},'SAR','Saudi Riyal',TRUE,TRUE,1,'finance-definer-tests',now());
            """);

        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.SuperuserConnectionString, $"""
            SET session_replication_role = replica;

            INSERT INTO public."LedgerAccounts"
                ("Id","BusinessUnitId","Code","Name","Category","NormalBalance","CurrencyId","IsControlAccount",
                 "AllowsManualPosting","IsActive","IsContraAccount","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},'1100','Accounts Receivable','Asset','Debit',91001,TRUE,TRUE,TRUE,FALSE,'idem-acct-1',repeat('a',64),1,'finance-definer-tests',now()),
                   (91002,{Tenant},'4000','Revenue','Revenue','Credit',91001,FALSE,TRUE,TRUE,FALSE,'idem-acct-2',repeat('b',64),1,'finance-definer-tests',now()),
                   (91003,{Tenant},'1000','Cash','Asset','Debit',91001,FALSE,TRUE,TRUE,FALSE,'idem-acct-3',repeat('c',64),1,'finance-definer-tests',now());

            INSERT INTO public."AccountingPeriods"
                ("Id","BusinessUnitId","FiscalYear","PeriodNumber","Name","StartsOn","EndsOn","Status",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},2026,8,'2026-08','2026-08-01','2026-08-31 23:59:59','Open','idem-period-1',repeat('d',64),1,'finance-definer-tests',now());

            INSERT INTO public."Customers" ("ID","Name","ImageURL","BUID","IsActive","CreatedBy","CreatedOn","ConcurrencyToken")
            VALUES (91001,'Definer Customer','',{Tenant},TRUE,'finance-definer-tests',now(),gen_random_uuid());

            INSERT INTO public."Setup_Master" ("SetupID","SetupType","SetupCode","SetupValue","BusinessUnitID","IsActive","CreatedBy","CreatedOn")
            VALUES (91001,'OrderStatus','CONFIRMED','Confirmed',{Tenant},TRUE,'finance-definer-tests',now());

            INSERT INTO public."Orders" ("ID","OrderNo","CustomerID","BusinessUnitID","StatusID","PaidAmount","TotalAmount","IsActive","CreatedBy","CreatedOn")
            VALUES (91001,'ORD-91001',91001,{Tenant},91001,0,2300,TRUE,'finance-definer-tests',now());

            INSERT INTO public."OrderItems" ("ID","OrderID","ProductID","Quantity","UnitPrice","Discount","TaxAmount","TotalAmount","CreatedBy","CreatedDate","IsActive")
            VALUES (91001,91001,1,20,100,0,150,1150,'finance-definer-tests',now(),TRUE);

            -- 91001 is the draft the issue journey issues. 91002 is already issued, so the write-off
            -- journey has a live balance to allocate against; its number is parked outside the
            -- counter's range so the allocation the fix restores cannot collide with it.
            INSERT INTO public."ReceivableDocuments"
                ("Id","BusinessUnitId","CustomerId","OrderId","CurrencyId","DocumentType","Status","DocumentNumber","DocumentDate","DueDate",
                 "IssuedOn","IssuedBy","SubTotal","DiscountAmount","TaxAmount","TotalAmount","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},91001,91001,91001,'Invoice','Draft',NULL,'2026-08-01','2026-09-01',
                    NULL,NULL,1000,0,150,1150,'idem-doc-1',repeat('e',64),1,'finance-definer-maker',now()),
                   (91002,{Tenant},91001,91001,91001,'Invoice','Issued','INV-2026-999999','2026-08-01','2026-09-01',
                    now(),'finance-definer-checker',1000,0,150,1150,'idem-doc-2',repeat('f',64),2,'finance-definer-maker',now());

            INSERT INTO public."ReceivableDocumentLines"
                ("Id","BusinessUnitId","ReceivableDocumentId","OrderItemId","Description","Quantity","UnitPrice","DiscountAmount","TaxAmount","LineTotal")
            VALUES (91001,{Tenant},91001,91001,'Definer line',10,100,0,150,1150),
                   (91002,{Tenant},91002,91001,'Definer issued line',10,100,0,150,1150);

            -- Two receipts: 91001 carries the approved refund, 91002 carries the kept promise. They
            -- are separate because a payment reserved by an active refund cannot be reversed, and
            -- one receipt doing both jobs would make the promise journey untestable.
            INSERT INTO public."CustomerPayments"
                ("Id","BusinessUnitId","CustomerId","CurrencyId","ReceiptNumber","Status","PaymentDate","Amount","Method",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn","AccountingBridgeRequired")
            VALUES (91001,{Tenant},91001,91001,'RCT-91001','Posted','2026-08-05',500,'BankTransfer','idem-pay-1',repeat('1',64),1,'finance-definer-tests',now(),FALSE),
                   (91002,{Tenant},91001,91001,'RCT-91002','Posted','2026-08-06',600,'BankTransfer','idem-pay-2',repeat('5',64),1,'finance-definer-tests',now(),FALSE);

            INSERT INTO public."CustomerRefunds"
                ("Id","BusinessUnitId","SourcePaymentId","CustomerId","CurrencyId","Status","RequestedExecutionDate","Amount",
                 "Method","DestinationReference","DestinationVerified","ReasonCode","Reason","EvidenceReference","PostingStatus",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn","ApprovedBy","ApprovedOn")
            VALUES (91001,{Tenant},91001,91001,91001,'Approved','2026-08-10',100,'BankTransfer','token:definer-destination-0001',TRUE,
                    'Overpayment','Customer overpaid the receipt','EVID-REFUND-0001','PendingDisbursement',
                    'idem-refund-1',repeat('2',64),2,'finance-definer-maker',now(),'finance-definer-approver',now());

            INSERT INTO public."ReceivableWriteOffs"
                ("Id","BusinessUnitId","CustomerId","CurrencyId","Status","AccountingDate","TotalAmount","ReasonCode","Reason",
                 "EvidenceReference","PostingStatus","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},91001,91001,'Draft','2026-08-10',1150,'Uncollectible','Debtor is insolvent and unreachable',
                    'EVID-WRITEOFF-0001','NotPosted','idem-wo-1',repeat('3',64),1,'finance-definer-maker',now());

            INSERT INTO public."WriteOffAllocations"
                ("Id","BusinessUnitId","ReceivableWriteOffId","ReceivableDocumentId","Amount","BalanceBefore","BalanceAfter")
            VALUES (91001,{Tenant},91001,91002,1150,1150,0);

            INSERT INTO public."CustomerStatements"
                ("Id","BusinessUnitId","CustomerId","CurrencyId","Status","PeriodStart","CutoffAt","CapturedOn","Revision",
                 "OpeningBalance","DebitTotal","CreditTotal","UnappliedCash","ClosingBalance","NetCustomerPosition",
                 "AgingCurrent","Aging1To30","Aging31To60","Aging61To90","AgingOver90","SourceFingerprint","SnapshotHash",
                 "ArtifactHash","ArtifactMediaType","ArtifactContent","GeneratorVersion","TemplateVersion",
                 "IssuerNameSnapshot","CustomerNameSnapshot","BillingAddressSnapshot","IdempotencyKey","RequestHash",
                 "Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},91001,91001,'Draft','2026-07-01','2026-08-01','2026-08-01 01:00:00',1,
                    0,1150,0,0,1150,1150,0,0,1150,0,0,repeat('4',64),repeat('5',64),
                    encode(digest(convert_to('definer-statement-artifact','UTF8'),'sha256'),'hex'),
                    'application/pdf','definer-statement-artifact','v1','t1',
                    'Definer Co','Definer Customer','Riyadh','idem-stmt-1',repeat('7',64),1,'finance-definer-tests',now());

            INSERT INTO public."DunningPolicies"
                ("Id","BusinessUnitId","PolicyVersion","Name","Status","JurisdictionCode","TimeZoneId","GraceDays","CadenceDays",
                 "MaximumStage","MinimumOverdueAmount","QuietHoursStart","QuietHoursEnd","TemplateVersion",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},1,'Definer dunning policy','Draft','SA','Asia/Riyadh',5,7,3,100,21,7,'t1',
                    'idem-policy-1',repeat('9',64),1,'finance-definer-tests',now());

            INSERT INTO public."DunningCases"
                ("Id","BusinessUnitId","CustomerId","CurrencyId","DunningPolicyId","CustomerStatementId","Status","CurrentStage",
                 "ExposureAtOpen","CurrentExposure","OldestDueDate","NextActionOn","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},91001,91001,91001,91001,'Open',1,1150,1150,'2026-07-15','2026-08-20','idem-case-1',repeat('8',64),1,'finance-definer-tests',now());

            INSERT INTO public."PromisesToPay"
                ("Id","BusinessUnitId","DunningCaseId","Amount","PromisedOn","DueOn","Status","EvidenceReference",
                 "Version","CreatedBy","CreatedOn","ClosedBy","ClosedOn","ClosureEvidenceReference",
                 "MatchedPaymentId","MatchedAmount","IdempotencyKey","RequestHash")
            VALUES (91001,{Tenant},91001,400,'2026-08-02','2026-08-20','Kept','EVID-PROMISE-0001',
                    1,'finance-definer-tests',now(),'finance-definer-collector',now(),'EVID-PROMISE-KEPT-0001',
                    91002,400,'idem-promise-1',repeat('0',64));

            SET session_replication_role = origin;
            """);
    }

    /// <summary>
    /// Idempotent because more than one test needs the book and xUnit gives no ordering. The
    /// conflicting call is a no-op rather than a failure, and it still goes through the trigger the
    /// first time.
    /// </summary>
    private async Task CreateLedgerBookAsync()
    {
        if (await CountAsync("public.\"LedgerBooks\"", $"\"BusinessUnitId\" = {Tenant}") > 0)
            return;

        await AsTenantAsync($"""
            INSERT INTO public."LedgerBooks"
                ("Id","BusinessUnitId","Name","FunctionalCurrencyId","TimeZoneId","FiscalYearStartMonth",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (91001,{Tenant},'Definer Book',91001,'Asia/Riyadh',1,'idem-book-1',repeat('a',64),1,'finance-definer-tests',now());
            """);
    }

    /// <summary>
    /// One journey, on the OWNER login role, behind the exact preamble
    /// <c>TenantRlsCommandInterceptor.CreateSetupCommand</c> writes on every tenant-scoped request.
    /// The transaction is real and is COMMITTED, because the evidence triggers are
    /// <c>DEFERRABLE INITIALLY DEFERRED</c> constraint triggers: a journey that rolls back never
    /// fires them, and a test that rolled back would have passed against the defect.
    /// </summary>
    private async Task AsTenantAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteOnAsync(connection, $"""
            SET LOCAL ROLE nexora_tenant_app;
            SELECT set_config('nexora.business_unit_id','{Tenant}',true);
            SELECT public.nexora_test_actor_envelope({Tenant},'finance-definer-tests');
            {sql}
            """);
        await transaction.CommitAsync();
    }

    private async Task<long> TenantScopedScalarAsync(long scope, string sql)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteOnAsync(connection, $"""
            SET LOCAL ROLE nexora_tenant_app;
            SELECT set_config('nexora.business_unit_id','{scope}',true);
            """);
        var value = await ScalarOnAsync(connection, sql);
        await transaction.RollbackAsync();
        return value;
    }

    private Task<long> CountAsync(string source, string predicate)
        => ScalarAsync($"SELECT count(*)::bigint FROM {source} WHERE {predicate};");

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        return await ScalarOnAsync(connection, sql);
    }

    private async Task<string?> StringAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static async Task<long> ScalarOnAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> StringOnAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static async Task ExecuteOnAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
