using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Players;

namespace Sadie.Networking.Events.Economy;

// Persistent, offline-safe inbox (player_inbox, D3). Writes a durable message and, if the player is
// online, pops a live alert plus pushes the updated unread count so the client badge bumps live.
public static class InboxService
{
    public sealed record InboxMessage(int Id, string Category, string Title, string Body, bool IsRead, string CreatedAt);

    public static async Task SendAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        long playerId, string category, string title, string body, string? payloadJson = null, bool pushAlert = true)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_inbox (player_id, category, title, body, payload, is_read, created_at) VALUES ({0}, {1}, {2}, {3}, {4}, 0, {5})",
            playerId, category, title, body,
            string.IsNullOrEmpty(payloadJson) ? (object) DBNull.Value : payloadJson,
            DateTime.Now);

        var online = playerRepository.GetPlayerLogicById(playerId);
        if (online?.NetworkObject != null)
        {
            // pushAlert=false lets the client present the notification itself (used for support replies,
            // so the in-client and Housekeeping paths notify the same way without double-popping).
            if (pushAlert)
                await online.NetworkObject.WriteToStreamAsync(new PlayerAlertWriter
                {
                    Message = string.IsNullOrEmpty(body) ? title : $"{title}\n\n{body}"
                });

            var unread = await GetUnreadCountAsync(dbContextFactory, playerId);
            await online.NetworkObject.WriteToStreamAsync(new InboxUnreadCountWriter { Count = unread });
        }
    }

    public static async Task<int> GetUnreadCountAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM player_inbox WHERE player_id = {0} AND is_read = 0", playerId).ToListAsync()).FirstOrDefault();
    }

    public static async Task MarkAllReadAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_inbox SET is_read = 1, read_at = {1} WHERE player_id = {0} AND is_read = 0", playerId, DateTime.Now);
    }

    public static async Task<List<InboxMessage>> GetMessagesAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var result = new List<InboxMessage>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, category, title, body, is_read, DATE_FORMAT(created_at, '%Y-%m-%d %H:%i') FROM player_inbox WHERE player_id = @pid ORDER BY created_at DESC LIMIT 50";
            var p = command.CreateParameter(); p.ParameterName = "@pid"; p.Value = playerId; command.Parameters.Add(p);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new InboxMessage(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3), reader.GetInt32(4) == 1, reader.GetString(5)));
            }
        }
        finally { await connection.CloseAsync(); }
        return result;
    }
}
