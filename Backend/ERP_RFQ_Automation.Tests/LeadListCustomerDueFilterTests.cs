using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner 2026-09-29: the work lists need filters "for client, date etc." so a rep can work one part
/// at a time. The Leads list takes a customer (or "none" for inquiries nobody has linked yet), a
/// bid-due-date button (Overdue · Next 7 days · Next 14 days) and, for a manager, one rep. All of it
/// is applied on the server, before the count, so the rows, the header count and Excel agree.
/// </summary>
public sealed class LeadListCustomerDueFilterTests
{
    private const long Bu = 97_100;
    // The real date: the window ignores a reader today more than a day from the server clock.
    private static readonly DateTime Today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);

    private static async Task<LeadRepository> SeedAsync(ErpRfqAutomationContext context)
    {
        Seed.Customer(context, 97_101, Bu, "Saudi Electricity Company");
        Seed.Customer(context, 97_102, Bu, "Saudi Aramco");
        foreach (var (id, first) in new[] { (97_191L, "Sara"), (97_192L, "Omar") })
            context.Users.Add(new User
            {
                Id = id, Buid = Bu, IsActive = true, FirstName = first, LastName = "Rep",
                Email = $"{first.ToLowerInvariant()}@nexora.test", PasswordHash = "x", ImageUrl = "", CreatedBy = "test",
            });
        await context.SaveChangesAsync();

        // id → (days from today to the bid due date, customer, owner)
        var rows = new (long Id, int? Days, long? Customer, long? Owner)[]
        {
            (97_111, -3, 97_101, 97_191),   // 3 days late
            (97_112, 0, 97_101, 97_191),    // due today: never overdue
            (97_113, 7, 97_102, 97_192),    // "7 days left": inside Next 7 days
            (97_114, 8, 97_102, null),
            (97_115, 14, null, null),       // no customer linked yet
            (97_116, 15, 97_101, 97_192),
            (97_117, null, 97_101, 97_191), // no deadline: only under Any
        };
        foreach (var r in rows)
        {
            var lead = Seed.Lead(context, r.Id, Bu);
            lead.BidClosingDate = r.Days is int d ? Today.AddDays(d).AddHours(14) : null;
            if (r.Customer is long c) lead.ResolveCommercialIdentity(c, null, "CONFIRMED");
            lead.AssignTo = r.Owner;
        }
        var placeholder = Seed.Lead(context, 97_118, Bu);
        placeholder.BidClosingDate = new DateTime(1900, 1, 1); // a placeholder, not a deadline
        await context.SaveChangesAsync();
        return new LeadRepository(context);
    }

    private static async Task<long[]> Ids(LeadRepository repo, string? customer = null, string? due = null, string view = "queue", DateTime? today = null)
    {
        var (rows, total) = await repo.GetLeadListAsync(1, 50, null, null, null, null, Bu, view: view,
            customerFilter: customer, due: due, today: today ?? Today);
        var ids = rows.Select(r => r.Id).OrderBy(id => id).ToArray();
        Assert.Equal(ids.Length, total); // the header count is the rows
        return ids;
    }

    [Fact]
    public async Task Bid_due_date_buttons_count_whole_days_from_the_readers_today()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 97_111 }, await Ids(repo, due: "overdue"));
        Assert.Equal(new long[] { 97_112, 97_113 }, await Ids(repo, due: "7d"));
        Assert.Equal(new long[] { 97_112, 97_113, 97_114, 97_115 }, await Ids(repo, due: "14d"));
        Assert.Equal(8, (await Ids(repo)).Length); // Any: everything, placeholder and no-deadline included
    }

    [Fact]
    public async Task A_reader_today_far_from_the_server_date_is_ignored()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        // A clock a year out would otherwise make every tender overdue.
        var overdue = await repo.GetLeadListAsync(1, 50, null, null, null, null, Bu, view: "queue",
            due: "overdue", today: DateTime.UtcNow.Date.AddYears(1));
        Assert.DoesNotContain(overdue.Item1, l => l.BidClosingDate > DateTime.UtcNow.AddDays(2));
    }

    [Fact]
    public async Task Customer_narrows_to_one_customer_and_none_finds_the_unlinked()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 97_111, 97_112, 97_116, 97_117 }, await Ids(repo, customer: "97101"));
        Assert.Equal(new long[] { 97_115, 97_118 }, await Ids(repo, customer: "none"));
        Assert.Equal(new long[] { 97_111 }, await Ids(repo, customer: "97101", due: "overdue"));
    }

    [Fact]
    public async Task A_manager_narrows_to_one_rep()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        Assert.Equal(new long[] { 97_113, 97_116 }, await Ids(repo, view: "queue,rep:97192"));
        Assert.Equal(new long[] { 97_113 }, await Ids(repo, view: "queue,rep:97192", due: "7d"));
        Assert.Empty(await Ids(repo, view: "queue,rep:abc")); // an unreadable rep matches nothing
    }

    [Fact]
    public async Task Customer_choices_are_the_customers_in_the_list_the_reader_is_on()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var repo = await SeedAsync(context);

        var (customers, noCustomer) = await repo.GetLeadListCustomersAsync(Bu, "queue");
        Assert.Equal(new[] { ("Saudi Aramco", 2), ("Saudi Electricity Company", 4) },
            customers.Select(c => (c.Name, c.Count)).ToArray());
        Assert.Equal(2, noCustomer);

        // Narrowed to one rep, only that rep's customers are offered.
        var (repCustomers, repNone) = await repo.GetLeadListCustomersAsync(Bu, "queue,rep:97191");
        Assert.Equal("Saudi Electricity Company", Assert.Single(repCustomers).Name);
        Assert.Equal(0, repNone);

        // With a date button on, the counts are what picking the customer would show.
        var (overdue, overdueNone) = await repo.GetLeadListCustomersAsync(Bu, "queue", due: "overdue", today: Today);
        Assert.Equal(("Saudi Electricity Company", 1), overdue.Select(c => (c.Name, c.Count)).Single());
        Assert.Equal(0, overdueNone);
    }
}
