using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class Gate03IntegrationContractTests
{
    [Fact]
    public void Email_ingest_identity_is_qualified_by_mailbox_configuration()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);
        var entity = context.Model.FindEntityType(typeof(EmailIngest))!;

        var index = Assert.Single(entity.GetIndexes(), x => x.IsUnique
            && x.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(EmailIngest.EmailConfigurationId), nameof(EmailIngest.MessageId) }));

        Assert.Equal("UQ_EmailIngests_EmailConfigurationID_MessageID", index.GetDatabaseName());
        Assert.DoesNotContain(entity.GetIndexes(), x => x.IsUnique
            && x.Properties.Count == 1 && x.Properties[0].Name == nameof(EmailIngest.MessageId));
    }
}
