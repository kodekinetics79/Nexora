using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Platform.DataAssets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The platform reading its own address, so that nobody has to know it.
///
/// <para><b>The defect.</b> <c>data.residency-isolation</c> needed a provider reference, a region
/// and a backup policy for the database every tenant lives in, and it asked the OPERATOR for all
/// three — four opaque fields in a dialog, per tenant. Moving them to
/// <c>Platform:DataBoundaries</c> environment variables fixed the repetition and not the problem:
/// an operator onboarding a customer still cannot answer "what is the Neon endpoint id", and now
/// needs a deploy and an infrastructure dashboard to say so. The process, at that same moment, is
/// holding an open connection to the database in question.</para>
///
/// <para><b>What must not have been traded for it.</b> The observation is a SUGGESTION — nothing
/// is registered against a tenant until an Owner confirms it, and the row records which of the two
/// happened. And the parsing is deliberately narrow: a host shape this code does not recognise
/// yields nothing at all, because a region invented out of a hostname is a residency claim nobody
/// made, which is the exact thing the manifest already refuses to do.</para>
/// </summary>
public sealed class DeploymentDescribesItsOwnDatabaseTests
{
    private static DatabaseSelfObservation Observe(string host)
    {
        using var db = ContextFor(host);
        return new DatabaseSelfObserver().Observe(db);
    }

    /// <summary>
    /// SQLite reports its `Data Source` as the DataSource, which is what the observer reads — so a
    /// host shape can be exercised without a live PostgreSQL server, using the same code path a
    /// deployed process takes.
    /// </summary>
    private static ErpRfqAutomationContext ContextFor(string host) => new(
        new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite($"Data Source={host}")
            .Options,
        new StubTenant(null));

    [Theory]
    /*
     * THE SHAPE THAT ACTUALLY REACHED PRODUCTION, and the reason this theory now carries four
     * spellings of one host. Npgsql describes a pooled connection as a URI —
     * "tcp://<host>:5432" — and the first version of this parser did DataSource.Split(':')[0],
     * which is correct for a bare "host:5432" and takes the SCHEME out of a URI. The console then
     * told an Owner, in its own confident words, that this deployment's database was called
     * "db-tcp" and asked them to correct the region by hand: the exact experience the feature
     * exists to remove, delivered with more authority than the form it replaced. A wrong answer
     * offered for confirmation is worse than no answer, because a plausible one gets confirmed.
     */
    [InlineData("tcp://ep-super-sea-admna6dt.c-2.us-east-1.aws.neon.tech:5432", "neon-ep-super-sea-admna6dt", "us-east-1")]
    [InlineData("tcp://ep-super-sea-admna6dt-pooler.c-2.us-east-1.aws.neon.tech:5432", "neon-ep-super-sea-admna6dt", "us-east-1")]
    [InlineData("ep-super-sea-admna6dt.c-2.us-east-1.aws.neon.tech:5432", "neon-ep-super-sea-admna6dt", "us-east-1")]
    // The shape Nexora's own production runs on, pooled and direct.
    [InlineData("ep-super-sea-admna6dt-pooler.c-2.us-east-1.aws.neon.tech", "neon-ep-super-sea-admna6dt", "us-east-1")]
    [InlineData("ep-super-sea-admna6dt.c-2.us-east-1.aws.neon.tech", "neon-ep-super-sea-admna6dt", "us-east-1")]
    [InlineData("ep-quiet-frost-12345.eu-central-1.aws.neon.tech", "neon-ep-quiet-frost-12345", "eu-central-1")]
    public void A_neon_host_names_its_own_endpoint_and_region(string host, string reference, string region)
    {
        var observed = Observe(host);

        Assert.Equal(reference, observed.OpaqueProviderReference);
        Assert.Equal(region, observed.Region);
        Assert.Equal("Neon", observed.ProviderName);
        Assert.True(observed.IsUsable);
        // Every value carries where it was read from: "us-east-1" alone is a claim, "us-east-1,
        // read from the host this process is connected to" is evidence. What it names is the HOST,
        // not the connection wrapper it arrived in — an operator reading "tcp://…:5432" back would
        // be looking at plumbing rather than at an address they can check.
        Assert.NotNull(observed.Host);
        Assert.Contains(observed.Host!, observed.Basis, StringComparison.Ordinal);
        Assert.DoesNotContain("tcp://", observed.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public void An_rds_host_names_its_instance_and_region()
    {
        var observed = Observe("nexora-prod.abc123xyz.eu-west-2.rds.amazonaws.com");

        Assert.Equal("rds-nexora-prod", observed.OpaqueProviderReference);
        Assert.Equal("eu-west-2", observed.Region);
        Assert.True(observed.IsUsable);
    }

    /// <summary>
    /// Azure names the server in the host and the region nowhere. Half an answer is reported as
    /// half an answer — the server reference is offered, the region is left for a human, and the
    /// panel says which is which.
    /// </summary>
    [Fact]
    public void An_azure_host_offers_the_server_and_refuses_to_guess_the_region()
    {
        var observed = Observe("nexora-prod.postgres.database.azure.com");

        Assert.Equal("azure-nexora-prod", observed.OpaqueProviderReference);
        Assert.Null(observed.Region);
        Assert.False(observed.IsUsable);
        Assert.Contains("region", observed.Basis, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other spellings a connection can arrive in. None of them may leave a scheme, a port, a
    /// credential or a path anywhere in what the console shows an operator.
    /// </summary>
    [Theory]
    [InlineData("postgres://nexora:secret@db01.internal.example.com:5432/neondb", "db01.internal.example.com")]
    [InlineData("tcp://db01.internal.example.com:5432", "db01.internal.example.com")]
    [InlineData("db01.internal.example.com,db02.internal.example.com", "db01.internal.example.com")]
    [InlineData("DB01.Internal.Example.COM", "db01.internal.example.com")]
    public void The_host_is_read_out_of_whatever_shape_the_connection_arrives_in(string dataSource, string expected)
    {
        var observed = Observe(dataSource);

        Assert.Equal(expected, observed.Host);
        // And nothing from the wrapper leaks into what an auditor would read.
        Assert.DoesNotContain("tcp", observed.OpaqueProviderReference ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", observed.Basis, StringComparison.Ordinal);
        Assert.DoesNotContain("5432", observed.Basis, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("tcp://127.0.0.1:5432")]
    // A Docker or Kubernetes service name. Legitimate, and still not a name an auditor could ever
    // resolve to a database — "db-postgres" as a residency reference means nothing to anybody.
    [InlineData("postgres")]
    [InlineData("tcp://db:5432")]
    public void A_bare_address_names_a_machine_rather_than_a_database_so_nothing_is_offered(string host)
    {
        var observed = Observe(host);

        Assert.Null(observed.OpaqueProviderReference);
        Assert.Null(observed.Region);
        Assert.False(observed.IsUsable);
    }

    /// <summary>
    /// The line this feature must not cross. An unrecognised host produces NO region — not a
    /// plausible one pulled out of a label — because a guessed region is a residency claim nobody
    /// made, and it would be recorded against every tenant on the deployment.
    /// </summary>
    [Fact]
    public void An_unrecognised_host_yields_no_region_at_all()
    {
        var observed = Observe("db01.internal.example.com");

        Assert.Null(observed.Region);
        Assert.False(observed.IsUsable);
        Assert.Contains("db01.internal.example.com", observed.Basis);
    }

    // ---- what the rest of the system resolves ------------------------------------------------

    private static PlatformDataBoundaryManifest Configuration(params (string Key, string Value)[] entries) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(x => x.Key, x => (string?)x.Value)).Build());

    private static readonly (string, string)[] ConfiguredEstate =
    [
        ("Platform:DataBoundaries:PostgreSqlTenantScope:OpaqueProviderReference", "from-configuration"),
        ("Platform:DataBoundaries:PostgreSqlTenantScope:Region", "eu-west-1"),
        ("Platform:DataBoundaries:PostgreSqlTenantScope:BackupPolicyReference", "config-policy"),
        ("Platform:DataBoundaries:PostgreSqlTenantScope:BackupPolicyVersion", "2")
    ];

    [Fact]
    public async Task What_an_owner_recorded_in_the_console_outranks_what_configuration_says()
    {
        using var harness = new ProvisioningHarness();
        await using var db = harness.Context();
        db.Set<PlatformDataBoundarySettings>().Add(new PlatformDataBoundarySettings
        {
            OpaqueProviderReference = "neon-ep-recorded", Region = "us-east-1",
            BackupPolicyReference = "pitr-7d", BackupPolicyVersion = 1,
            Basis = ProvenanceBases.ObservedAndConfirmed, Reason = "Confirmed from the live connection",
            RecordedBy = "owner@nexora.app", RecordedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolved = new ResolvedPlatformDataBoundaryManifest(db, Configuration(ConfiguredEstate));
        var primary = resolved.For(TenantDataAssetTypes.PostgreSqlTenantScope);

        Assert.NotNull(primary);
        // The console wins not because it is more trustworthy but because it is the one an operator
        // can CORRECT. A wrong configuration value cannot be fixed from the screen that shows it.
        Assert.Equal("neon-ep-recorded", primary!.OpaqueProviderReference);
        Assert.Equal("us-east-1", primary.Region);
        Assert.Equal(DataBoundarySources.Console, resolved.Source);
    }

    [Fact]
    public async Task Configuration_still_answers_when_nobody_has_recorded_anything()
    {
        using var harness = new ProvisioningHarness();
        await using var db = harness.Context();

        var resolved = new ResolvedPlatformDataBoundaryManifest(db, Configuration(ConfiguredEstate));

        Assert.Equal(DataBoundarySources.Configuration, resolved.Source);
        Assert.Equal("from-configuration",
            resolved.For(TenantDataAssetTypes.PostgreSqlTenantScope)!.OpaqueProviderReference);
    }

    [Fact]
    public async Task A_deployment_that_has_said_nothing_anywhere_still_declares_nothing()
    {
        using var harness = new ProvisioningHarness();
        await using var db = harness.Context();

        var resolved = new ResolvedPlatformDataBoundaryManifest(db, Configuration());

        Assert.False(resolved.IsConfigured);
        Assert.Equal(DataBoundarySources.None, resolved.Source);
        Assert.Null(resolved.For(TenantDataAssetTypes.PostgreSqlTenantScope));
    }

    /// <summary>
    /// The console row describes the primary database only. A deployment that declared its object
    /// store in configuration keeps it — the two are not alternatives, and dropping the object
    /// store because somebody filled in the database form would take deletion certification
    /// backwards without anybody asking for it.
    /// </summary>
    [Fact]
    public async Task Recording_the_database_does_not_discard_the_other_boundaries_configuration_declared()
    {
        using var harness = new ProvisioningHarness();
        await using var db = harness.Context();
        db.Set<PlatformDataBoundarySettings>().Add(new PlatformDataBoundarySettings
        {
            OpaqueProviderReference = "neon-ep-recorded", Region = "us-east-1",
            BackupPolicyReference = "pitr-7d", BackupPolicyVersion = 1,
            Basis = ProvenanceBases.ObservedAndConfirmed, Reason = "Confirmed from the live connection",
            RecordedBy = "owner@nexora.app", RecordedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var resolved = new ResolvedPlatformDataBoundaryManifest(db, Configuration([
            .. ConfiguredEstate,
            ("Platform:DataBoundaries:ObjectStorage:OpaqueProviderReference", "backblaze-nexora"),
            ("Platform:DataBoundaries:ObjectStorage:Region", "us-east-005"),
            ("Platform:DataBoundaries:ObjectStorage:BackupPolicyReference", "bucket-versioning"),
            ("Platform:DataBoundaries:ObjectStorage:BackupPolicyVersion", "1")
        ]));

        Assert.Equal("neon-ep-recorded",
            resolved.For(TenantDataAssetTypes.PostgreSqlTenantScope)!.OpaqueProviderReference);
        Assert.Equal("backblaze-nexora",
            resolved.For(TenantDataAssetTypes.ObjectStorage)!.OpaqueProviderReference);
        Assert.Equal(2, resolved.Boundaries.Count);
    }
}
