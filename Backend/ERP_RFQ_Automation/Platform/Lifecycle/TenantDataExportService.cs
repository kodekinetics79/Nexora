using System.Security.Cryptography;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>One section of the export: an entity, and — for line items — the parent it hangs off.</summary>
/// <param name="Entity">CLR name of the entity. Resolved to a table through the EF model, never
/// hardcoded: the mapped names diverge from the CLR names across this schema
/// (<c>Rfq</c> → <c>"RFQ"</c>, <c>SetupMaster</c> → <c>"Setup_Master"</c>).</param>
/// <param name="ParentEntity">Set when the table carries no tenant column of its own.</param>
public sealed record TenantExportSection(string Entity, string? ParentEntity = null);

public sealed record TenantExportSectionResult(
    string Section, string Table, long Rows, string ScopedBy, IReadOnlyList<string> RedactedColumns);

public sealed record TenantExportRefusal(string Section, string Reason);

public sealed record TenantExportDocument(
    byte[] Content,
    string ContentSha256,
    string Format,
    long TotalRows,
    IReadOnlyList<TenantExportSectionResult> Sections,
    IReadOnlyList<TenantExportRefusal> Refused,
    bool RowLevelSecurityEnforced);

/// <summary>
/// Produces the file a departing customer is entitled to: their commercial records, in one
/// self-describing JSON document, scoped so hard that another tenant's rows are not merely absent
/// but unreachable.
///
/// <para><b>Why the section list is curated and the reset's is derived.</b> <c>TenantDataReset</c>
/// discovers its tables from the catalogue precisely so a newly added table cannot be missed — a
/// clean slate that quietly is not one is its worst failure. An export inverts that: it is a
/// CONTRACTUAL deliverable, and a new internal table appearing in a customer's hand-over file
/// without anybody deciding it should is a disclosure, not a completeness win. Extraction
/// telemetry, outbox rows, dead-letter payloads and lease bookkeeping are ours; leads, quotes,
/// orders and the master data behind them are theirs. So the list is written down, and anything
/// on it that cannot be resolved is REFUSED loudly in the manifest rather than skipped.</para>
///
/// <para><b>Three independent reasons a foreign tenant's row cannot appear.</b>
/// <list type="number">
/// <item>The query runs as <c>nexora_tenant_app</c> with <c>nexora.business_unit_id</c> set, so
/// PostgreSQL's own row-level-security policies filter the result. That role is NOBYPASSRLS: a
/// mistake in the SQL below cannot widen what the database is willing to return.</item>
/// <item>Every statement carries an explicit parameterised predicate on the tenant column, which
/// is resolved from the live catalogue rather than assumed — the same 42703 lesson the reset
/// learned from <c>BusinessUnitID</c> vs <c>BusinessUnitId</c> vs <c>BUID</c>.</item>
/// <item>Every emitted row is re-checked against the requested tenant before it is written, and a
/// single mismatch aborts the whole export. It costs one comparison per row and it is the only
/// check that would survive both of the above being wrong.</item>
/// </list>
/// A section whose table has no tenant column and no parent is structurally impossible to scope,
/// so it is refused outright rather than exported unfiltered.</para>
///
/// <para><b>Line items are joined to their parent, not filtered directly.</b> <c>LeadItems</c>,
/// <c>QuoteItems</c>, <c>RFQItems</c>, <c>OrderItems</c> and <c>ShipmentItems</c> carry no tenant
/// column — they are scoped through their header. Exporting headers without lines would hand a
/// customer quotes with no prices, which is not their data in any useful sense. They are reached
/// through an inner join to the parent, whose visibility RLS has already decided, so a child row
/// is exportable only if the database itself agrees its parent is.</para>
/// </summary>
public sealed class TenantDataExportService(
    ErpRfqAutomationContext context,
    IConfiguration configuration,
    ILogger<TenantDataExportService> logger)
{
    public const string FormatIdentifier = "nexora.tenant-export.v1";

    /// <summary>
    /// The tenant's commercial record, in the order somebody reading the file would want it:
    /// the demand chain, then the fulfilment chain, then the master data both resolve against,
    /// then the workspace configuration that explains the codes inside them.
    /// </summary>
    public static readonly IReadOnlyList<TenantExportSection> Sections =
    [
        // Demand: the enquiry, its lines, and how its status moved.
        new(nameof(Lead)),
        new(nameof(LeadItem), nameof(Lead)),
        new(nameof(LeadStatusHistory)),
        new(nameof(Rfq)),
        new(nameof(Rfqitem), nameof(Rfq)),
        new(nameof(Quote)),
        new(nameof(QuoteItem), nameof(Quote)),

        // Fulfilment: what was ordered and what shipped.
        new(nameof(Order)),
        new(nameof(OrderItem), nameof(Order)),
        new(nameof(Shipment)),
        new(nameof(ShipmentItem), nameof(Shipment)),

        // Counterparties and catalogue: without these the documents above are lists of ids.
        new(nameof(Customer)),
        new(nameof(Contact)),
        new(nameof(Supplier)),
        new(nameof(SupplierQuotedItem)),
        new(nameof(Product)),
        new(nameof(ProductCategory)),
        new(nameof(ProductSubCategory)),
        new(nameof(Warehouse)),

        // Workspace configuration: the roles, statuses, currencies and units of measure every
        // code in the documents above resolves against, plus the letterhead their quotes printed.
        new(nameof(SetupMaster)),
        new(nameof(RolePermission)),
        new(nameof(Team)),
        new(nameof(UserGroup)),
        new(nameof(User)),
        new(nameof(QuoteConfiguration)),
        new(nameof(LeadReferenceConfiguration)),
        new(nameof(EmailConfiguration))
    ];

    /// <summary>
    /// Column-name fragments whose values are never exported.
    ///
    /// <para>An export is a copy of the customer's records, not a copy of our credential store.
    /// <c>Email_Configurations.Password</c> is a live mailbox password; <c>Users.PasswordHash</c>
    /// is an offline-crackable credential for accounts that may still exist elsewhere. Both would
    /// otherwise travel inside a file that gets emailed, forwarded and left in a downloads folder.
    /// Matched on a fragment rather than an exact list so a column added tomorrow called
    /// <c>RefreshToken</c> is redacted before anybody has to remember it exists.</para>
    /// </summary>
    private static readonly string[] RedactedColumnFragments =
        ["password", "secret", "token", "credential", "apikey", "api_key", "privatekey"];

    private const string RedactedPlaceholder = "[redacted: credential]";
    private const string TenantVerificationAlias = "__nexora_tenant";

    public async Task<TenantExportDocument> ExportAsync(
        Tenant tenant, long businessUnitId, string actorEmail, CancellationToken cancellationToken)
    {
        if (!context.Database.IsNpgsql())
            throw TenantOffboardingRefusedException.NotSupported(
                "Tenant export is implemented against PostgreSQL. It reads the live catalogue to "
                + "resolve tenant columns and runs under the row-level-security role, neither of "
                + "which exists on the portable provider.");

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw TenantOffboardingRefusedException.NotSupported(
                "No runtime database connection is configured, so an export cannot run here.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // READ ONLY is not decoration. An export must be incapable of changing the data it is
        // describing, and the customer is entitled to a file that represents one consistent
        // instant rather than a smear across however long the extraction took.
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY;", cancellationToken);

        var rlsEnforced = await ApplyTenantScopeAsync(connection, transaction, businessUnitId, cancellationToken);

        var sections = new List<TenantExportSectionResult>();
        var refused = new List<TenantExportRefusal>();

        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("data");
            writer.WriteStartObject();
            foreach (var section in Sections)
            {
                Plan plan;
                try
                {
                    plan = await PlanSectionAsync(connection, transaction, section, cancellationToken);
                }
                catch (TenantExportSectionRefusedException refusal)
                {
                    refused.Add(new TenantExportRefusal(section.Entity, refusal.Message));
                    continue;
                }

                writer.WritePropertyName(section.Entity);
                var result = await WriteSectionAsync(
                    connection, transaction, plan, businessUnitId, writer, cancellationToken);
                sections.Add(result);
            }
            writer.WriteEndObject();

            // The manifest is written LAST but read first: it describes what the reader is
            // holding, including the sections that are absent and why, so "my orders aren't in
            // here" has an answer inside the file rather than in a support ticket.
            writer.WritePropertyName("manifest");
            WriteManifest(writer, tenant, businessUnitId, actorEmail, sections, refused, rlsEnforced);

            writer.WriteEndObject();
        }

        await transaction.CommitAsync(cancellationToken);

        var content = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        logger.LogInformation(
            "Tenant export produced for tenant {TenantId}: {Rows} row(s) across {Sections} section(s), "
            + "{Bytes} byte(s), sha256 {Hash}.",
            tenant.Id, sections.Sum(s => s.Rows), sections.Count, content.Length, hash);

        return new TenantExportDocument(
            content, hash, FormatIdentifier, sections.Sum(s => s.Rows), sections, refused, rlsEnforced);
    }

    /// <summary>
    /// Puts the connection inside the tenant's row-level-security scope, exactly as
    /// <c>TenantRlsCommandInterceptor</c> does on the request path.
    ///
    /// <para>The role is mandatory. A contractual export is a bulk disclosure and must not fall
    /// back to application predicates when the database isolation boundary is unavailable.</para>
    /// </summary>
    private async Task<bool> ApplyTenantScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long businessUnitId,
        CancellationToken cancellationToken)
    {
        await using (var probe = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role);", connection, transaction))
        {
            probe.Parameters.AddWithValue("role", MultiTenancy.TenantRlsCommandInterceptor.TenantRole);
            if (await probe.ExecuteScalarAsync(cancellationToken) is not true)
            {
                throw new InvalidOperationException(
                    $"Tenant export refused because the required row-level-security role " +
                    $"'{MultiTenancy.TenantRlsCommandInterceptor.TenantRole}' is unavailable.");
            }
        }

        await using var scope = new NpgsqlCommand(
            $"SET LOCAL ROLE {MultiTenancy.TenantRlsCommandInterceptor.TenantRole};"
            + " SELECT set_config('nexora.business_unit_id', @tenant, true);", connection, transaction);
        scope.Parameters.AddWithValue(
            "tenant", businessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await scope.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private sealed class TenantExportSectionRefusedException(string message) : Exception(message);

    /// <summary>A resolved section: the SQL to run, and what to verify each row against.</summary>
    private sealed record Plan(
        string Section, string Table, string Sql, string ScopedBy, IReadOnlyList<string> Redacted);

    /// <summary>
    /// Turns a declared section into a statement, resolving every identifier through the EF model
    /// and the live catalogue so nothing here is a spelling assumption.
    /// </summary>
    private async Task<Plan> PlanSectionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TenantExportSection section,
        CancellationToken cancellationToken)
    {
        var entity = FindEntity(section.Entity);
        var (schema, table) = TableOf(entity, section.Entity);

        var columns = await ColumnsAsync(connection, transaction, schema, table, cancellationToken);
        if (columns.Count == 0)
            throw new TenantExportSectionRefusedException(
                $"public.\"{table}\" has no columns in this database; the section cannot be exported.");

        var redacted = columns.Where(IsCredentialColumn).ToList();
        var projection = string.Join(", ", columns.Select(c =>
            IsCredentialColumn(c) ? $"'{RedactedPlaceholder}'::text AS \"{c}\"" : $"child.\"{c}\""));

        if (section.ParentEntity is null)
        {
            var tenantColumn = TenantColumn(columns)
                ?? throw new TenantExportSectionRefusedException(
                    $"\"{schema}\".\"{table}\" carries no tenant column, so no predicate can scope it "
                    + "to one tenant. Exporting it unfiltered would disclose every tenant's rows.");

            return new Plan(section.Entity, $"{schema}.{table}",
                $"""
                 SELECT {projection}, child."{tenantColumn}" AS "{TenantVerificationAlias}"
                 FROM "{schema}"."{table}" child
                 WHERE child."{tenantColumn}" = @tenant;
                 """,
                tenantColumn, redacted);
        }

        var parent = FindEntity(section.ParentEntity);
        var (parentSchema, parentTable) = TableOf(parent, section.ParentEntity);
        var parentColumns = await ColumnsAsync(
            connection, transaction, parentSchema, parentTable, cancellationToken);

        var parentTenantColumn = TenantColumn(parentColumns)
            ?? throw new TenantExportSectionRefusedException(
                $"\"{parentSchema}\".\"{parentTable}\" carries no tenant column, so its children "
                + $"cannot be scoped through it.");

        var (childKey, parentKey) = JoinColumns(entity, parent, section);

        return new Plan(section.Entity, $"{schema}.{table}",
            $"""
             SELECT {projection}, parent."{parentTenantColumn}" AS "{TenantVerificationAlias}"
             FROM "{schema}"."{table}" child
             JOIN "{parentSchema}"."{parentTable}" parent ON parent."{parentKey}" = child."{childKey}"
             WHERE parent."{parentTenantColumn}" = @tenant;
             """,
            $"{parentTable}.{parentTenantColumn}", redacted);
    }

    private async Task<TenantExportSectionResult> WriteSectionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Plan plan, long businessUnitId,
        Utf8JsonWriter writer, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(plan.Sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", businessUnitId);

        long rows = 0;
        writer.WriteStartArray();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var verificationOrdinal = reader.GetOrdinal(TenantVerificationAlias);

            while (await reader.ReadAsync(cancellationToken))
            {
                // The third guard. If RLS were misconfigured AND the predicate were wrong, this is
                // what still stops another customer's row reaching this file — and it fails the
                // whole export rather than dropping the row, because a silently shortened export
                // is indistinguishable from a correct one.
                var owner = reader.IsDBNull(verificationOrdinal)
                    ? (long?)null
                    : Convert.ToInt64(reader.GetValue(verificationOrdinal));
                if (owner != businessUnitId)
                    throw new InvalidOperationException(
                        $"Export aborted: {plan.Table} returned a row owned by tenant "
                        + $"{owner?.ToString() ?? "none"} while exporting tenant {businessUnitId}.");

                writer.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (i == verificationOrdinal) continue;
                    writer.WritePropertyName(reader.GetName(i));
                    WriteValue(writer, reader, i);
                }
                writer.WriteEndObject();
                rows++;
            }
        }

        writer.WriteEndArray();
        return new TenantExportSectionResult(plan.Section, plan.Table, rows, plan.ScopedBy, plan.Redacted);
    }

    private static void WriteManifest(
        Utf8JsonWriter writer, Tenant tenant, long businessUnitId, string actorEmail,
        IReadOnlyList<TenantExportSectionResult> sections, IReadOnlyList<TenantExportRefusal> refused,
        bool rlsEnforced)
    {
        writer.WriteStartObject();
        writer.WriteString("format", FormatIdentifier);
        writer.WriteNumber("tenantId", tenant.Id);
        writer.WriteString("tenantSlug", tenant.Slug);
        writer.WriteString("tenantName", tenant.Name);
        if (tenant.LegalName is not null) writer.WriteString("legalName", tenant.LegalName);
        if (tenant.RegistrationNumber is not null)
            writer.WriteString("registrationNumber", tenant.RegistrationNumber);
        if (tenant.TaxNumber is not null) writer.WriteString("taxNumber", tenant.TaxNumber);
        if (tenant.CountryCode is not null) writer.WriteString("countryCode", tenant.CountryCode);
        if (tenant.BaseCurrencyCode is not null)
            writer.WriteString("baseCurrencyCode", tenant.BaseCurrencyCode);
        writer.WriteNumber("businessUnitId", businessUnitId);
        writer.WriteString("generatedOnUtc", DateTime.UtcNow);
        writer.WriteString("generatedBy", actorEmail);
        writer.WriteBoolean("rowLevelSecurityEnforced", rlsEnforced);
        writer.WriteNumber("totalRows", sections.Sum(s => s.Rows));

        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        foreach (var section in sections)
        {
            writer.WriteStartObject();
            writer.WriteString("section", section.Section);
            writer.WriteString("table", section.Table);
            writer.WriteNumber("rows", section.Rows);
            writer.WriteString("scopedBy", section.ScopedBy);
            writer.WritePropertyName("redactedColumns");
            writer.WriteStartArray();
            foreach (var column in section.RedactedColumns) writer.WriteStringValue(column);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("refusedSections");
        writer.WriteStartArray();
        foreach (var refusal in refused)
        {
            writer.WriteStartObject();
            writer.WriteString("section", refusal.Section);
            writer.WriteString("reason", refusal.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("notice");
        writer.WriteStartArray();
        writer.WriteStringValue(
            "Credential columns (mailbox passwords, password hashes, tokens) are replaced with "
            + $"'{RedactedPlaceholder}'. They are not part of a records export.");
        writer.WriteStringValue(
            "Binary column contents are summarised rather than inlined. Uploaded document bytes "
            + "are governed by the tenant's evidence retention policy, not by this export.");
        writer.WriteStringValue(TenantOffboardingDisclosure.DeletionIsNotErasure);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    // ---------------------------------------------------------------- model + catalogue lookups

    private IEntityType FindEntity(string clrName) =>
        context.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == clrName)
        ?? throw new TenantExportSectionRefusedException(
            $"'{clrName}' is not an entity in the current model, so the section cannot be resolved "
            + "to a table. Remove it from the export section list or restore the entity.");

    private static (string Schema, string Table) TableOf(IEntityType entity, string clrName)
    {
        var table = entity.GetTableName()
            ?? throw new TenantExportSectionRefusedException(
                $"'{clrName}' is not mapped to a table.");
        return (entity.GetSchema() ?? "public", table);
    }

    /// <summary>
    /// The foreign key from a line-item entity to its header, resolved through the model so the
    /// join is whatever the mapping actually says rather than a guess at a column name.
    /// </summary>
    private static (string ChildColumn, string ParentColumn) JoinColumns(
        IEntityType child, IEntityType parent, TenantExportSection section)
    {
        var foreignKey = child.GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType == parent && fk.Properties.Count == 1)
            ?? throw new TenantExportSectionRefusedException(
                $"'{section.Entity}' has no single-column foreign key to '{section.ParentEntity}', so "
                + "its rows cannot be scoped through their parent.");

        var childStore = StoreObjectIdentifier.Create(child, StoreObjectType.Table)!.Value;
        var parentStore = StoreObjectIdentifier.Create(parent, StoreObjectType.Table)!.Value;

        var childColumn = foreignKey.Properties[0].GetColumnName(childStore)
            ?? throw new TenantExportSectionRefusedException(
                $"'{section.Entity}' foreign key column is not mapped.");
        var parentColumn = foreignKey.PrincipalKey.Properties[0].GetColumnName(parentStore)
            ?? throw new TenantExportSectionRefusedException(
                $"'{section.ParentEntity}' key column is not mapped.");

        return (childColumn, parentColumn);
    }

    /// <summary>
    /// The table's columns, read from the live catalogue in ordinal order.
    ///
    /// <para>Read from the database rather than the model on purpose: the model's property names
    /// and the mapped columns diverge across this schema, and several tables carry columns EF does
    /// not know about at all. A projection built from property names produces 42703 on exactly
    /// the tables that matter most.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ColumnsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = @schema AND table_name = @table
            ORDER BY ordinal_position;
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var column = reader.GetString(0);
            // Identifiers come from the catalogue and cannot carry an injected quote; the guard is
            // here so that stays true if this ever reads from somewhere else.
            if (!column.Contains('"')) columns.Add(column);
        }
        return columns;
    }

    private static string? TenantColumn(IReadOnlyList<string> columns) =>
        columns.FirstOrDefault(c =>
            c.Equals("BusinessUnitId", StringComparison.OrdinalIgnoreCase)
            || c.Equals("BUID", StringComparison.OrdinalIgnoreCase));

    private static bool IsCredentialColumn(string column) =>
        RedactedColumnFragments.Any(fragment =>
            column.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static void WriteValue(Utf8JsonWriter writer, NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) { writer.WriteNullValue(); return; }

        var value = reader.GetValue(ordinal);
        switch (value)
        {
            case bool b: writer.WriteBooleanValue(b); break;
            case short s: writer.WriteNumberValue(s); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case decimal d: writer.WriteNumberValue(d); break;
            case double d: writer.WriteNumberValue(d); break;
            case float f: writer.WriteNumberValue(f); break;
            case DateTime dt: writer.WriteStringValue(dt); break;
            case DateTimeOffset dto: writer.WriteStringValue(dto); break;
            case Guid g: writer.WriteStringValue(g); break;
            case string str: writer.WriteStringValue(str); break;

            // Inlining a base64 blob per row turns a readable records file into something no
            // spreadsheet or script can open, and document bytes are governed by the tenant's
            // evidence retention policy rather than by this export.
            case byte[] bytes: writer.WriteStringValue($"[binary: {bytes.Length} byte(s)]"); break;

            default: writer.WriteStringValue(value.ToString()); break;
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
