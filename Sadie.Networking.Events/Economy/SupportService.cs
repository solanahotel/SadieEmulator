using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Networking.Events.Writers;

namespace Sadie.Networking.Events.Economy;

// Support tickets: private back-and-forth between a player and staff. Users open/reply/close their own;
// staff (in-client admins or Housekeeping) reply/close any and can restrict abusers from opening tickets.
public static class SupportService
{
    public const int MaxOpenTickets = 5;

    public sealed record TicketView(int Id, string Subject, string Category, string Status, string OwnerName, int Messages, string UpdatedAt);
    public sealed record MessageView(string SenderName, bool IsStaff, string Body, string CreatedAt);
    public sealed record TicketDetail(int Id, string Subject, string Category, string Status, long OwnerId, string OwnerName, IReadOnlyList<MessageView> Messages);

    public static async Task<bool> IsBannedAsync(IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM support_ticket_bans WHERE player_id = {0}", playerId).ToListAsync()).FirstOrDefault() > 0;
    }

    public static async Task<(bool Ok, string Error, int TicketId)> OpenTicketAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player, string subject, string category, string body)
    {
        subject = (subject ?? "").Trim();
        body = (body ?? "").Trim();
        if (subject.Length == 0 || body.Length == 0) return (false, "Please enter a subject and a message.", 0);
        if (subject.Length > 120) subject = subject[..120];

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        if (await IsBannedAsync(dbContextFactory, player.Id))
            return (false, "You've been restricted from opening tickets.", 0);

        var open = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM support_tickets WHERE player_id = {0} AND status = 'open'", player.Id).ToListAsync()).FirstOrDefault();
        if (open >= MaxOpenTickets) return (false, $"You already have {MaxOpenTickets} open tickets. Please close one first.", 0);

        var ticketId = await InsertReturningIdAsync(dbContext,
            "INSERT INTO support_tickets (player_id, subject, category, status, created_at, updated_at) VALUES ({0}, {1}, {2}, 'open', {3}, {3})",
            player.Id, subject, string.IsNullOrEmpty(category) ? "general" : category, DateTime.Now);

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO support_ticket_messages (ticket_id, sender_id, sender_name, is_staff, body, created_at) VALUES ({0}, {1}, {2}, 0, {3}, {4})",
            ticketId, player.Id, player.Username, body, DateTime.Now);

        return (true, "", ticketId);
    }

    public static async Task<List<TicketView>> ListTicketsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long viewerId, bool staffViewAll)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var result = new List<TicketView>();
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT t.id, t.subject, t.category, t.status, p.username, " +
                "(SELECT COUNT(*) FROM support_ticket_messages m WHERE m.ticket_id = t.id), DATE_FORMAT(t.updated_at, '%Y-%m-%d %H:%i') " +
                "FROM support_tickets t JOIN players p ON p.id = t.player_id " +
                (staffViewAll ? "" : "WHERE t.player_id = @v ") +
                "ORDER BY (t.status = 'open') DESC, t.updated_at DESC LIMIT 100";
            if (!staffViewAll) { var v = cmd.CreateParameter(); v.ParameterName = "@v"; v.Value = viewerId; cmd.Parameters.Add(v); }
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                result.Add(new TicketView(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6)));
        }
        finally { await conn.CloseAsync(); }
        return result;
    }

    public static async Task<TicketDetail?> GetTicketAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long viewerId, bool isStaff, int ticketId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            int id; string subject, category, status, ownerName; long ownerId;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT t.id, t.subject, t.category, t.status, t.player_id, p.username FROM support_tickets t JOIN players p ON p.id = t.player_id WHERE t.id = @id";
                var ip = cmd.CreateParameter(); ip.ParameterName = "@id"; ip.Value = ticketId; cmd.Parameters.Add(ip);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return null;
                id = reader.GetInt32(0); subject = reader.GetString(1); category = reader.GetString(2);
                status = reader.GetString(3); ownerId = reader.GetInt64(4); ownerName = reader.GetString(5);
            }

            if (!isStaff && ownerId != viewerId) return null; // access control

            var messages = new List<MessageView>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT sender_name, is_staff, body, DATE_FORMAT(created_at, '%Y-%m-%d %H:%i') FROM support_ticket_messages WHERE ticket_id = @id ORDER BY id ASC";
                var ip = cmd.CreateParameter(); ip.ParameterName = "@id"; ip.Value = ticketId; cmd.Parameters.Add(ip);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    messages.Add(new MessageView(reader.GetString(0), reader.GetInt32(1) == 1, reader.GetString(2), reader.GetString(3)));
            }
            return new TicketDetail(id, subject, category, status, ownerId, ownerName, messages);
        }
        finally { await conn.CloseAsync(); }
    }

    public static async Task<(bool Ok, string Error)> PostMessageAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        long senderId, string senderName, bool isStaff, int ticketId, string body)
    {
        body = (body ?? "").Trim();
        if (body.Length == 0) return (false, "Enter a message.");

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var rows = await dbContext.Database.SqlQueryRaw<long>(
            "SELECT player_id AS Value FROM support_tickets WHERE id = {0} AND status = 'open'", ticketId).ToListAsync();
        if (rows.Count == 0) return (false, "That ticket is closed or no longer exists.");
        var ownerId = rows[0];

        if (!isStaff && ownerId != senderId) return (false, "That isn't your ticket.");

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO support_ticket_messages (ticket_id, sender_id, sender_name, is_staff, body, created_at) VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            ticketId, senderId, senderName, isStaff ? 1 : 0, body, DateTime.Now);
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE support_tickets SET updated_at = {1} WHERE id = {0}", ticketId, DateTime.Now);

        // Notify the owner when staff replies + live-push the updated conversation if they're online.
        if (isStaff && ownerId != senderId)
        {
            await InboxService.SendAsync(playerRepository, dbContextFactory, ownerId, "support",
                $"Staff replied to ticket #{ticketId}", "Open the Help → Support centre to read the reply.", pushAlert: false);

            var online = playerRepository.GetPlayerLogicById(ownerId);
            if (online?.NetworkObject != null)
            {
                var detail = await GetTicketAsync(dbContextFactory, ownerId, false, ticketId);
                if (detail != null) await online.NetworkObject.WriteToStreamAsync(new SupportTicketUpdateWriter { Ticket = detail });
            }
        }

        return (true, "");
    }

    public static async Task<(bool Ok, string Error)> CloseTicketAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long viewerId, bool isStaff, int ticketId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var rows = await dbContext.Database.SqlQueryRaw<long>(
            "SELECT player_id AS Value FROM support_tickets WHERE id = {0}", ticketId).ToListAsync();
        if (rows.Count == 0) return (false, "Ticket not found.");
        if (!isStaff && rows[0] != viewerId) return (false, "That isn't your ticket.");

        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE support_tickets SET status = 'closed', closed_by = {1}, updated_at = {2} WHERE id = {0} AND status = 'open'",
            ticketId, isStaff ? "staff" : "user", DateTime.Now);
        return (true, "");
    }

    public static async Task SetBanAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long targetId, bool banned, string? reason)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        if (banned)
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO support_ticket_bans (player_id, reason, created_at) VALUES ({0}, {1}, {2}) ON DUPLICATE KEY UPDATE reason = VALUES(reason)",
                targetId, (object?) reason ?? DBNull.Value, DateTime.Now);
        else
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM support_ticket_bans WHERE player_id = {0}", targetId);
    }

    // Returns true once per player (the first time) so the guide pops up for new users only.
    public static async Task<bool> ConsumeGuideFlagAsync(IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT IGNORE INTO player_guide_seen (player_id, seen_at) VALUES ({0}, {1})", playerId, DateTime.Now);
        return affected > 0;
    }

    private static async Task<int> InsertReturningIdAsync(SadieDbContext dbContext, string sql, params object[] args)
    {
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(sql, args);
            return (int) (await dbContext.Database.SqlQueryRaw<long>("SELECT LAST_INSERT_ID() AS Value").ToListAsync()).First();
        }
        finally { await conn.CloseAsync(); }
    }
}
