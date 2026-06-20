using Microsoft.EntityFrameworkCore;
using Sadie.Db;

namespace Sadie.Networking.Events.Moderation;

// Durable audit trail of the Call-for-Help / moderation ticket lifecycle (table: cfh_tickets). The
// CfhIssueStore queue is in-memory and ephemeral; this records every report and how it was handled
// (picked / released / closed with a resolution + the handling moderator) so it can be traced back.
// Each in-memory CfhIssue carries the DbId of its row so later actions update the right ticket.
public static class CfhTicketLog
{
    public const string ResolutionUseless = "useless";
    public const string ResolutionAbusive = "abusive";
    public const string ResolutionResolved = "resolved";

    // Client CloseIssuesMessageComposer constants: 1 = USELESS, 2 = ABUSIVE, 3 = RESOLVED.
    public static string MapResolution(int resolutionType) => resolutionType switch
    {
        1 => ResolutionUseless,
        2 => ResolutionAbusive,
        3 => ResolutionResolved,
        _ => "closed"
    };

    // Inserts the open ticket and returns its auto-increment id (0 on failure). Opens the connection
    // explicitly so the follow-up LAST_INSERT_ID() reads this insert on the same connection.
    public static async Task<int> CreateAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory,
        long reporterId, long reportedId, int roomId, int categoryId, string message)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.OpenConnectionAsync();

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO cfh_tickets (reporter_id, reported_id, room_id, category_id, message, state, created_at) VALUES ({0}, {1}, {2}, {3}, {4}, 'open', {5})",
                reporterId,
                reportedId > 0 ? reportedId : DBNull.Value,
                roomId > 0 ? roomId : DBNull.Value,
                categoryId, message, DateTime.Now);

            var rows = await dbContext.Database
                .SqlQueryRaw<long>("SELECT LAST_INSERT_ID() AS Value")
                .ToListAsync();

            return (int) rows.FirstOrDefault();
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    public static async Task SetPickedAsync(IDbContextFactory<SadieDbContext> dbContextFactory, int ticketId, long handlerId)
    {
        if (ticketId <= 0) return;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE cfh_tickets SET state = 'picked', handler_id = {1}, updated_at = {2} WHERE id = {0}",
            ticketId, handlerId, DateTime.Now);
    }

    public static async Task SetReleasedAsync(IDbContextFactory<SadieDbContext> dbContextFactory, int ticketId)
    {
        if (ticketId <= 0) return;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE cfh_tickets SET state = 'open', handler_id = NULL, updated_at = {1} WHERE id = {0}",
            ticketId, DateTime.Now);
    }

    public static async Task SetClosedAsync(IDbContextFactory<SadieDbContext> dbContextFactory, int ticketId, long handlerId, string resolution)
    {
        if (ticketId <= 0) return;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var now = DateTime.Now;
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE cfh_tickets SET state = 'closed', handler_id = {1}, resolution = {2}, updated_at = {3}, closed_at = {3} WHERE id = {0}",
            ticketId, handlerId, resolution, now);
    }

    // Lifetime count of calls for help the player has submitted, and how many were closed as abusive.
    public static async Task<int> CfhCountAsync(SadieDbContext dbContext, long reporterId) =>
        (await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM cfh_tickets WHERE reporter_id = {0}", reporterId)
            .ToListAsync()).FirstOrDefault();

    public static async Task<int> AbusiveCfhCountAsync(SadieDbContext dbContext, long reporterId) =>
        (await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM cfh_tickets WHERE reporter_id = {0} AND resolution = {1}", reporterId, ResolutionAbusive)
            .ToListAsync()).FirstOrDefault();
}
