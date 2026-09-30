namespace ERP_RFQ_Automation.Repositories
{
    /// <summary>
    /// The date buttons on the work lists (Overdue · Next 7 days · Next 14 days · Any), defined once.
    ///
    /// <para>Deadlines are stored as the buyer's wall clock (timestamp without time zone) and every
    /// Deadline cell counts whole calendar days from the reader's today to the deadline's own day
    /// (utils/dates.ts calendarDaysUntil). The window is therefore day-granular and counted from the
    /// same "today" the cells use, which the page sends: "Due today" is never Overdue, and "7 days
    /// left" is inside Next 7 days. A supplied today more than a day away from the server's UTC date
    /// is ignored, so a wrong clock cannot move the window by weeks.</para>
    /// </summary>
    public static class ListDueWindow
    {
        /// <summary>Anything before this is a placeholder date, not a deadline (the pages read it as none).</summary>
        public static readonly DateTime Floor = new(2000, 1, 1);

        public static (DateTime? From, DateTime? Before)? Resolve(string? due, DateTime? readerToday, DateTime utcNow)
        {
            // Wall-clock values, like the columns they are compared with.
            var today = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Unspecified);
            if (readerToday.HasValue && Math.Abs((readerToday.Value.Date - today).TotalDays) <= 1)
                today = DateTime.SpecifyKind(readerToday.Value.Date, DateTimeKind.Unspecified);

            return due?.Trim().ToLowerInvariant() switch
            {
                "overdue" => (Floor, today),
                "7d" => (today, today.AddDays(8)),
                "14d" => (today, today.AddDays(15)),
                _ => null,
            };
        }
    }
}
