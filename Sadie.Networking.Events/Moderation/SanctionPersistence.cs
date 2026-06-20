using Microsoft.EntityFrameworkCore;
using Sadie.Db;

namespace Sadie.Networking.Events.Moderation;

// Durable, historical record of mod sanctions (table: player_sanctions). The in-memory stores
// (TradeLockStore / GlobalMuteStore) remain the hot-path used by enforcement; this is the source
// of truth for the mod-tool "user info" counts and for re-seeding the stores on login so timed
// sanctions survive an emulator restart. Bans are intentionally NOT logged here — they have their
// own player_bans table, which the panel's ban count already reads.
public static class SanctionPersistence
{
    public const string TypeCaution = "caution";
    public const string TypeMute = "mute";
    public const string TypeTradeLock = "tradelock";

    public static async Task LogAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory,
        long creatorId,
        long playerId,
        string type,
        string reason,
        DateTime? expiresAt,
        int? ticketId = null)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // ticket_id links the sanction back to the CFH ticket the mod was handling, when there is one.
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_sanctions (creator_id, player_id, type, reason, created_at, expires_at, ticket_id) VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})",
            creatorId, playerId, type, reason, DateTime.Now,
            expiresAt.HasValue ? expiresAt.Value : DBNull.Value,
            ticketId.HasValue ? ticketId.Value : DBNull.Value);
    }

    // Total historical count of a sanction type against a player.
    public static async Task<int> CountAsync(SadieDbContext dbContext, long playerId, string type) =>
        (await dbContext.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM player_sanctions WHERE player_id = {0} AND type = {1}", playerId, type)
            .ToListAsync()).FirstOrDefault();

    // Expiry of the player's currently-active sanction of this type (latest still in the future), or
    // null if none is active. Used both for the panel's expiry display and for login re-seeding.
    public static async Task<DateTime?> ActiveExpiryAsync(SadieDbContext dbContext, long playerId, string type)
    {
        var rows = await dbContext.Database
            .SqlQueryRaw<DateTime>(
                "SELECT expires_at AS Value FROM player_sanctions WHERE player_id = {0} AND type = {1} AND expires_at IS NOT NULL AND expires_at > {2} ORDER BY expires_at DESC LIMIT 1",
                playerId, type, DateTime.Now)
            .ToListAsync();

        return rows.Count > 0 ? rows[0] : null;
    }
}
