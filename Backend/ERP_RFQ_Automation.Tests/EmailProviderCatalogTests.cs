using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The provider catalogue is a code catalogue for the same reason <c>ModuleCatalog</c> is: a seeded
/// table is only correct on the day it is written, and these entries are the settings an operator
/// would otherwise hunt out of a provider's help pages. These tests are what make it safe to be
/// code — every rule that decides whether a preset can actually work is asserted here, so a preset
/// that cannot connect fails the build instead of shipping as a recommended default.
/// </summary>
public sealed class EmailProviderCatalogTests
{
    [Fact]
    public void Every_preset_host_is_one_the_SSRF_policy_would_actually_dial()
    {
        // A preset the endpoint policy then refuses is a guaranteed dead end presented as a
        // recommended default: the operator picks it, saves, and the connection test fails at the
        // very first stage on settings the product itself supplied.
        foreach (var provider in EmailProviderCatalog.All)
        foreach (var preset in provider.Presets)
            Assert.True(MailEndpointPolicy.IsAllowedEndpoint(preset.Host, preset.Port),
                $"Preset '{provider.Key}' {preset.Direction}/{preset.Transport} " +
                $"({preset.Host}:{preset.Port}) is refused by MailEndpointPolicy.");
    }

    [Fact]
    public void No_preset_asks_an_operator_to_send_a_credential_in_the_clear()
    {
        // GoDaddy, among others, publishes cleartext relays on port 25 and port 80. They work, so
        // they end up in help pages, and a catalogue that repeated them would be recommending that
        // a mailbox password cross the internet unencrypted.
        foreach (var provider in EmailProviderCatalog.All)
        foreach (var preset in provider.Presets)
            Assert.NotEqual(MailTlsMode.None, preset.Tls);
    }

    [Fact]
    public void Every_inbound_preset_is_implicit_TLS_because_the_alternative_is_silent_cleartext()
    {
        // The trap this closes. For IMAP the runtime reads UseSsl=false as SecureSocketOptions.None
        // — NOT as STARTTLS. So an inbound preset declaring StartTls would be translated into a
        // cleartext session carrying the mailbox password, with nothing on the screen saying so.
        // Until the poller learns to negotiate STARTTLS on 143, inbound presets are 993 or nothing.
        foreach (var provider in EmailProviderCatalog.All)
        {
            if (provider.Inbound is not { } inbound) continue;
            Assert.Equal(MailTlsMode.Implicit, inbound.Tls);
            Assert.True(inbound.UseSsl);
        }
    }

    [Theory]
    [InlineData(MailTransport.Imap, MailTlsMode.Implicit, true)]
    [InlineData(MailTransport.Smtp, MailTlsMode.Implicit, true)]
    // The asymmetry that broke the old preset table: SMTP on 587 needs the flag OFF, because the
    // runtime reads false as STARTTLS. The previous Microsoft 365 preset shipped 587 with the flag
    // ON, which asks for implicit TLS on a port that speaks plaintext until STARTTLS.
    [InlineData(MailTransport.Smtp, MailTlsMode.StartTls, false)]
    public void The_stored_UseSsl_flag_is_derived_per_transport_not_shared(
        MailTransport transport, MailTlsMode tls, bool expected)
    {
        var preset = new EmailConnectionPreset(
            transport == MailTransport.Imap ? MailDirection.Inbound : MailDirection.Outbound,
            transport, "mail.example.com", 587, tls);

        Assert.Equal(expected, preset.UseSsl);
    }

    [Fact]
    public void Every_preset_port_and_TLS_mode_agree_with_the_probes_own_encryption_advice()
    {
        // MailboxConnectionProbe warns when the port and the encryption setting disagree. A preset
        // that trips its own product's warning is a preset nobody should have shipped, so the two
        // are pinned against each other rather than maintained in parallel.
        foreach (var provider in EmailProviderCatalog.All)
        foreach (var preset in provider.Presets.Where(x => x.IsMailProtocol))
        {
            var protocol = preset.Transport == MailTransport.Smtp
                ? MailboxConnectionProbe.Smtp
                : MailboxConnectionProbe.Imap;

            var advice = MailboxConnectionProbe.EncryptionAdvice(protocol, preset.Port, preset.UseSsl);
            Assert.True(advice is null,
                $"Preset '{provider.Key}' {preset.Transport}:{preset.Port} would warn: {advice?.Detail}");
        }
    }

    [Fact]
    public void The_providers_the_CEO_and_every_onboarding_customer_actually_use_are_all_present()
    {
        // Named individually rather than counted: a count passes while the wrong nine are present.
        // GoDaddy heads the list because it is the one whose settings were being hunted by hand.
        string[] required =
        [
            "godaddy", "microsoft365", "google", "sendgrid", "amazonses",
            "postmark", "mailgun", "zoho", "custom"
        ];

        foreach (var key in required)
            Assert.True(EmailProviderCatalog.Find(key) is not null, $"Provider '{key}' is missing.");
    }

    [Fact]
    public void GoDaddy_fills_in_both_directions_which_is_the_whole_point_of_the_table()
    {
        // The concrete promise: choosing GoDaddy fills host, port and TLS for reading AND sending.
        // These are the values GoDaddy publishes, and a change to them should be a deliberate edit
        // with this test in front of it.
        var godaddy = EmailProviderCatalog.Find("godaddy")!;

        Assert.Equal("imap.secureserver.net", godaddy.Inbound!.Host);
        Assert.Equal(993, godaddy.Inbound.Port);
        Assert.Equal(MailTlsMode.Implicit, godaddy.Inbound.Tls);

        Assert.Equal("smtpout.secureserver.net", godaddy.OutboundSmtp!.Host);
        Assert.Equal(465, godaddy.OutboundSmtp.Port);
        Assert.Equal(MailTlsMode.Implicit, godaddy.OutboundSmtp.Tls);

        // The limit that turns into "quotes stopped going out after lunch" if nobody says it first.
        Assert.False(string.IsNullOrWhiteSpace(godaddy.SendingLimit));
    }

    [Fact]
    public void Microsoft_365_declares_the_two_things_that_produce_most_mailbox_support_tickets()
    {
        var microsoft = EmailProviderCatalog.Find("microsoft365")!;

        Assert.True(microsoft.SmtpAuthDisabledByDefault);
        Assert.True(microsoft.RequiresAppPassword);
        Assert.False(string.IsNullOrWhiteSpace(microsoft.InboundEnablementNote));

        // 587/STARTTLS out, 993/implicit in. The pair a single boolean could not express.
        Assert.Equal(MailTlsMode.StartTls, microsoft.OutboundSmtp!.Tls);
        Assert.False(microsoft.OutboundSmtp.UseSsl);
        Assert.True(microsoft.Inbound!.UseSsl);
    }

    [Fact]
    public void Google_declares_the_app_password_requirement_that_no_amount_of_retyping_fixes()
    {
        var google = EmailProviderCatalog.Find("google")!;
        Assert.True(google.RequiresAppPassword);
        Assert.Contains(MailAuthMode.AppPassword, google.SupportedAuthModes);
    }

    [Fact]
    public void Send_only_providers_are_never_offered_as_a_tenant_mailbox()
    {
        // Email_Configurations stores a host, port and password. Offering SendGrid there produces a
        // row that saves cleanly, has nowhere to put an API key, and can never connect.
        var tenantKeys = EmailProviderCatalog.ForTenantMailbox.Select(x => x.Key).ToList();

        Assert.DoesNotContain("sendgrid", tenantKeys);
        Assert.DoesNotContain("amazonses", tenantKeys);
        Assert.DoesNotContain("postmark", tenantKeys);
        Assert.DoesNotContain("mailgun", tenantKeys);

        Assert.Contains("godaddy", tenantKeys);
        Assert.Contains("microsoft365", tenantKeys);
        Assert.Contains(EmailProviderCatalog.CustomKey, tenantKeys);
    }

    [Fact]
    public void Every_tenant_mailbox_provider_can_both_read_and_send()
    {
        foreach (var provider in EmailProviderCatalog.ForTenantMailbox
                     .Where(x => x.Key != EmailProviderCatalog.CustomKey))
        {
            Assert.NotNull(provider.Inbound);
            Assert.NotNull(provider.OutboundSmtp);
        }
    }

    [Fact]
    public void Keys_are_unique_stable_and_lowercase_because_they_are_persisted()
    {
        // The key is written against a mailbox row and echoed by the API. A duplicate would make
        // Find() return whichever entry the dictionary happened to keep; a mixed-case key would
        // make an exact-match lookup against stored data fail on a round trip.
        var keys = EmailProviderCatalog.All.Select(x => x.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key =>
        {
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.True(key.Length <= EmailProviderModelBuilderExtensions.ProviderKeyMaxLength,
                $"Provider key '{key}' does not fit the {EmailProviderModelBuilderExtensions.ProviderKeyMaxLength}-character column.");
        });
    }

    [Fact]
    public void Every_provider_carries_guidance_because_a_form_without_it_is_just_a_form()
    {
        Assert.All(EmailProviderCatalog.All, provider =>
            Assert.False(string.IsNullOrWhiteSpace(provider.Guidance),
                $"Provider '{provider.Key}' has no guidance."));

        Assert.All(EmailProviderCatalog.All, provider =>
            Assert.NotEmpty(provider.SupportedAuthModes));
    }

    [Fact]
    public void Custom_is_present_carries_no_preset_and_is_the_only_one_that_does_not()
    {
        var custom = EmailProviderCatalog.Find(EmailProviderCatalog.CustomKey)!;
        Assert.Empty(custom.Presets);

        Assert.All(EmailProviderCatalog.All.Where(x => x.Key != EmailProviderCatalog.CustomKey),
            provider => Assert.NotEmpty(provider.Presets));
    }

    // ---- host inference ------------------------------------------------------------------

    [Theory]
    [InlineData("smtpout.secureserver.net", "godaddy")]
    [InlineData("imap.secureserver.net", "godaddy")]
    [InlineData("outlook.office365.com", "microsoft365")]
    [InlineData("smtp.office365.com", "microsoft365")]
    [InlineData("mail.tenant.onmicrosoft.outlook.com", "microsoft365")]
    [InlineData("imap.gmail.com", "google")]
    [InlineData("smtp.sendgrid.net", "sendgrid")]
    [InlineData("email-smtp.eu-west-1.amazonaws.com", "amazonses")]
    [InlineData("smtp.postmarkapp.com", "postmark")]
    [InlineData("smtp.eu.mailgun.org", "mailgun")]
    [InlineData("imap.zoho.eu", "zoho")]
    // Trailing dots are legal in an FQDN and must not defeat the match.
    [InlineData("smtp.office365.com.", "microsoft365")]
    public void An_existing_mailbox_still_gets_provider_specific_remedies_without_a_stored_key(
        string host, string expected)
    {
        // Every mailbox in the product predates the catalogue and has no provider recorded. If
        // inference did not work, the sentence that resolves most tickets — "Microsoft disables
        // SMTP submission per mailbox by default" — would be unavailable on exactly the rows that
        // need it.
        Assert.Equal(expected, EmailProviderCatalog.InferKeyFromHost(host));
    }

    [Theory]
    [InlineData("mail.acme-industrial.sa")]
    [InlineData("")]
    [InlineData(null)]
    // Suffix matching, not Contains: attaching Google's remedies to an unrelated host would send
    // the operator to generate an App Password on an account that has nothing to do with it.
    [InlineData("gmail.com.attacker.example")]
    [InlineData("notsecureserver.net.example.org")]
    public void An_unknown_or_lookalike_host_infers_nothing(string? host)
        => Assert.Null(EmailProviderCatalog.InferKeyFromHost(host));

    // ---- the shape the UI consumes ---------------------------------------------------------

    [Fact]
    public void The_capability_view_carries_a_useSsl_value_per_direction_not_one_shared_flag()
    {
        var dto = EmailProviderCatalog.Find("microsoft365")!.ToDto();

        Assert.True(dto.Inbound!.UseSsl);
        Assert.False(dto.OutboundSmtp!.UseSsl);
        Assert.True(dto.SmtpAuthDisabledByDefault);
        Assert.True(dto.RequiresAppPassword);
        Assert.Contains("username", dto.RequiredFields);
        Assert.Contains("password", dto.RequiredFields);
    }

    [Fact]
    public void An_api_key_provider_asks_for_an_api_key_rather_than_a_password_box()
    {
        var dto = EmailProviderCatalog.Find("sendgrid")!.ToDto();

        Assert.Contains("apiKey", dto.RequiredFields);
        Assert.False(dto.SupportsInbound);
        Assert.True(dto.SupportsOutbound);
        Assert.False(dto.SupportsTenantMailbox);
        Assert.NotNull(dto.OutboundApi);
    }

    [Fact]
    public void Enums_cross_the_wire_as_names_not_ordinals()
    {
        // This API registers no global string-enum converter. Without the per-enum attributes these
        // serialise as 0..3 and every client has to hardcode an ordinal that changes the moment a
        // transport or an auth mode is inserted in the middle.
        var json = JsonSerializer.Serialize(EmailProviderCatalog.Find("microsoft365")!.ToDto());

        Assert.Contains("\"Implicit\"", json, StringComparison.Ordinal);
        Assert.Contains("\"StartTls\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Smtp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"AppPassword\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tls\":0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"transport\":0", json, StringComparison.Ordinal);
    }

    [Fact]
    public void No_type_the_catalogue_returns_can_carry_a_secret()
    {
        // The catalogue is served to anyone with View on Email & SMTP. A field named for a
        // credential on any of these types is one careless mapping away from being populated.
        Type[] outbound = [typeof(EmailProviderCapabilityDto), typeof(EmailEndpointDto)];

        // String-typed only. A boolean named RequiresAppPassword answers "is one needed?" and can
        // hold nothing; it is the string-shaped fields that could ever carry a value.
        foreach (var type in outbound)
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.PropertyType == typeof(string) &&
                     (p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                      p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                      p.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                      (p.Name.Contains("key", StringComparison.OrdinalIgnoreCase) &&
                       p.Name is not nameof(EmailProviderCapabilityDto.Key))));
    }

    // ---- the legacy preset surface the mailbox screen still binds to -----------------------

    [Fact]
    public void The_legacy_preset_endpoint_now_serves_the_catalogue_rather_than_a_second_copy()
    {
        var presets = LegacyPresets();

        Assert.Equal(
            EmailProviderCatalog.ForTenantMailbox.Select(x => x.Key).ToArray(),
            presets.Select(x => x.Key).ToArray());
    }

    [Fact]
    public void A_provider_whose_two_directions_disagree_says_so_in_the_first_sentence()
    {
        // The legacy DTO has ONE encryption flag for both directions. Microsoft 365 needs two. The
        // shape cannot be fixed without a frontend change, so the only honest channel left is the
        // guidance text — and it has to lead, because it is shown as a transient toast.
        var microsoft = LegacyPresets().Single(x => x.Key == "microsoft365");

        Assert.StartsWith("For SENDING", microsoft.Guidance, StringComparison.Ordinal);
        Assert.Contains("587", microsoft.Guidance, StringComparison.Ordinal);
        Assert.Contains("OFF", microsoft.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_whose_two_directions_agree_carries_no_such_warning()
    {
        var godaddy = LegacyPresets().Single(x => x.Key == "godaddy");
        Assert.DoesNotContain("For SENDING", godaddy.Guidance, StringComparison.Ordinal);
    }

    private static IReadOnlyList<MailboxPresetDTO> LegacyPresets() =>
        (IReadOnlyList<MailboxPresetDTO>)typeof(MailboxProbeResult).Assembly
            .GetType("ERP_RFQ_Automation.Controllers.MailboxPresets")!
            .GetField("All", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    // ---- the provider column, and why it is not on yet -------------------------------------

    [Fact]
    public void The_provider_column_is_absent_from_the_model_until_the_splice_lands()
    {
        // The safety property, asserted rather than trusted. Email_Configurations feeds live RFQ
        // ingestion, and EF materialises the whole entity: a column present in the model but absent
        // from the database fails EVERY read of the table with 42703 — the poller, the quote send
        // path and the mailbox screen at once. Splice and migration land together or not at all.
        using var context = new UnsplicedContext();
        var entity = context.Model.FindEntityType(typeof(EmailConfiguration))!;

        Assert.Null(entity.FindProperty(EmailProviderModelBuilderExtensions.ProviderKeyColumnName));
    }

    [Fact]
    public void Applying_the_splice_adds_the_column_as_a_nullable_shadow_property()
    {
        // The other half: one call turns the column on, with the width and nullability the
        // migration must match. Nullable because every mailbox that exists predates the catalogue
        // and had no provider chosen for it.
        using var context = new SplicedContext();
        var property = context.Model.FindEntityType(typeof(EmailConfiguration))!
            .FindProperty(EmailProviderModelBuilderExtensions.ProviderKeyColumnName);

        Assert.NotNull(property);
        Assert.Equal(EmailProviderModelBuilderExtensions.ProviderKeyColumnName, property.GetColumnName());
        Assert.Equal(EmailProviderModelBuilderExtensions.ProviderKeyMaxLength, property.GetMaxLength());
        Assert.True(property.IsNullable,
            "The column must be nullable: every existing mailbox row has no provider recorded.");
    }

    [Fact]
    public void The_entity_carries_no_CLR_property_that_would_silently_fail_to_persist()
    {
        // A [NotMapped] ProviderKey on EmailConfiguration would be worse than none: EF's annotation
        // convention outranks fluent configuration, so it would stay unmapped even after the splice
        // and the next person to assign it would watch the value vanish on save with no error.
        Assert.DoesNotContain(typeof(EmailConfiguration).GetProperties(),
            p => p.Name.Equals(EmailProviderModelBuilderExtensions.ProviderKeyColumnName, StringComparison.Ordinal));
    }

    /// <summary>
    /// A model containing nothing but the mailbox entity, so the splice can be observed on its own
    /// rather than inferred from a context that configures two hundred others.
    ///
    /// <para>Two CONTEXT TYPES rather than one taking a flag, because EF caches a built model
    /// against the context type: a single type would have the first test to run decide the model
    /// the second one sees, and the pair would pass or fail on their execution order.</para>
    /// </summary>
    private abstract class ProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlite("DataSource=:memory:");

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<EmailConfiguration>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.ToTable("Email_Configurations");
                entity.Ignore(x => x.BusinessUnit);
                entity.Ignore(x => x.EmailIngests);
            });
    }

    private sealed class UnsplicedContext : ProbeContext;

    private sealed class SplicedContext : ProbeContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyEmailProviderModel();
        }
    }
}
