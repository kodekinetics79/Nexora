using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A quote created through the service the API actually calls comes out of the write path with a
/// named owner — or with an honest NULL.
///
/// <para>The scoped funnel divides headline money by <c>Quote.OwnerUserId</c>. A column nothing
/// writes is a column that reads NULL for every quote made after the backfill, which would quietly
/// empty every rep's funnel while leaving the tenant total intact — the kind of defect that looks
/// like "the dashboard is broken" long after the cause. So the write path is driven here, not the
/// attribution helper on its own.</para>
/// </summary>
public sealed class QuoteOwnershipWritePathTests
{
    private const long Tenant = 5_500;
    private const long RepId = 5_510;
    private const long TwinOne = 5_511;
    private const long TwinTwo = 5_512;
    private const long CustomerId = 5_520;
    private const long DraftStatusId = 5_530;

    private static readonly DateTime Now = new(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    // The rep's own email and the "First Last" the client sends both name one person.
    [InlineData("rep@nexora.invalid", RepId)]
    [InlineData("REP@Nexora.Invalid", RepId)]
    [InlineData("Sam North", RepId)]
    // A service actor, a stranger, and a name two people answer to name nobody attributable.
    [InlineData("System", null)]
    [InlineData("someone@elsewhere.invalid", null)]
    [InlineData("Twin Person", null)]
    public async Task Creating_a_quote_records_the_owner_only_when_the_actor_names_exactly_one_user(
        string createdBy, long? expectedOwner)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        await SeedAsync(context);

        var service = new QuoteService(context, new SilentEmailService(), null!);
        var created = await service.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            BusinessUnitId = Tenant,
            CustomerId = CustomerId,
            StatusId = DraftStatusId,
            CreatedBy = createdBy,
            QuoteDate = Now,
            QuoteItems =
            [
                new QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Gasket spiral wound",
                    Quantity = 10m,
                    UnitOfMeasure = "EA",
                    UnitPrice = 10m,
                    TotalAmount = 100m
                }
            ]
        });

        var stored = await context.Quotes.AsNoTracking().SingleAsync(q => q.Id == created.Id);
        Assert.Equal(expectedOwner, stored.OwnerUserId);
        // The free text is kept exactly as it was. The owner is an addition to the record, not a
        // replacement for the actor string the audit trail reads.
        Assert.Equal(createdBy, stored.CreatedBy);
    }

    private static async Task SeedAsync(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        context.Users.AddRange(
            User(RepId, "Sam", "North", "rep@nexora.invalid"),
            User(TwinOne, "Twin", "Person", "twin.one@nexora.invalid"),
            User(TwinTwo, "Twin", "Person", "twin.two@nexora.invalid"));
        Seed.Customer(context, CustomerId, Tenant, "North Account");
        await context.SaveChangesAsync();
    }

    private sealed class SilentEmailService : IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }

    private static User User(long id, string first, string last, string email) => new()
    {
        Id = id, FirstName = first, LastName = last, Email = email, PasswordHash = "x",
        ImageUrl = "n/a", Buid = Tenant, IsActive = true, CreatedBy = "tests", CreatedOn = Now
    };
}
