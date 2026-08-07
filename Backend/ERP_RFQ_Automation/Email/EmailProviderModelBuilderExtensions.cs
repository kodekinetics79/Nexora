using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Email;

/// <summary>
/// EF configuration for the provider dimension on an existing tenant mailbox, kept in the owning
/// module so the context needs exactly one delegating call — the same splice discipline
/// <c>ApplyPlatformEmailModel</c> and <c>ApplyTenantOnboardingModel</c> use.
///
/// <para><b>What the column records, and why inference is not enough on its own.</b>
/// <c>EmailProviderCatalog.InferKeyFromHost</c> recovers the provider for almost every row that
/// exists today, and the connection test uses it so no mailbox is left without provider-specific
/// remedies. What inference cannot recover is a CHOICE: an operator who picked "Something else"
/// against a host that merely looks like a known provider, or a customer reaching Microsoft 365
/// through a vanity CNAME. Storing the answer makes the guidance on the screen and the remedy
/// offered by a failed test agree with what the operator actually selected.</para>
///
/// <para><b>Additive by construction.</b> <c>Email_Configurations</c> is the table live RFQ
/// ingestion reads from. Nothing here renames, retypes or re-keys anything: it adds one nullable
/// column and nothing else, so every row written before it existed stays valid and no backfill is
/// needed.</para>
///
/// <para><b>Nothing calls this yet, and that is the safety mechanism.</b> EF materialises the whole
/// entity on every read of this table, so a column present in the model but absent from the
/// database fails the poller, the quote send path and the mailbox screen at once with
/// <c>42703 column does not exist</c>. Splicing this call and running the migration are therefore
/// one change, and until both land the column exists in neither place:
/// <code>
/// // Models/ErpRfqAutomationContext.cs, in OnModelCreating, immediately after the
/// // modelBuilder.Entity&lt;EmailConfiguration&gt;(...) block:
/// modelBuilder.ApplyEmailProviderModel();
/// </code>
/// <code>
/// -- migration Up()
/// ALTER TABLE "Email_Configurations" ADD COLUMN "ProviderKey" character varying(32) NULL;
/// </code>
/// </para>
///
/// <para><b>A shadow property, not a CLR property, deliberately.</b> A <c>[NotMapped]</c> property
/// on <c>EmailConfiguration</c> would be worse than nothing: EF's annotation convention outranks
/// fluent configuration, so the attribute would still win after this call and the next person to
/// write <c>row.ProviderKey = "godaddy"</c> would watch it silently not persist. Read and write it
/// as <c>EF.Property&lt;string?&gt;(row, "ProviderKey")</c>, or — once the column exists in the
/// database — promote it to a real property on the entity in the same change as the migration and
/// replace the shadow declaration below with <c>entity.Property(e =&gt; e.ProviderKey)</c>.</para>
/// </summary>
public static class EmailProviderModelBuilderExtensions
{
    /// <summary>Wide enough for every catalogue key with room to spare, and narrow enough that the
    /// column cannot quietly become a place to store something else. Asserted against the catalogue
    /// by <c>EmailProviderCatalogTests</c>.</summary>
    public const int ProviderKeyMaxLength = 32;

    public const string ProviderKeyColumnName = "ProviderKey";

    public static ModelBuilder ApplyEmailProviderModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<EmailConfiguration>(entity =>
        {
            // Nullable, because every mailbox that exists predates the catalogue and has no
            // provider recorded. A NOT NULL column with a default would claim a provider was chosen
            // for rows where nobody chose one, and the inference path handles them honestly.
            entity.Property<string>(ProviderKeyColumnName)
                .HasColumnName(ProviderKeyColumnName)
                .HasMaxLength(ProviderKeyMaxLength)
                .IsRequired(false);

            // Deliberately NOT a foreign key or a check constraint against the catalogue. The
            // catalogue is code and changes with a deploy; a database constraint on it would make
            // retiring a provider a migration against live tenant rows, and would turn an unknown
            // key — which the read path already degrades gracefully on — into a write failure on a
            // mailbox that is otherwise fine.
        });

        return modelBuilder;
    }
}
