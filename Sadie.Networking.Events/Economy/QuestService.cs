using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;

namespace Sadie.Networking.Events.Economy;

// Daily Quests: per-player daily progress against tunable definitions (daily_quests). Progress is tracked
// automatically by gameplay hooks; the player claims the credit reward once a quest's goal is met.
public static class QuestService
{
    public sealed record QuestView(string Code, string Name, string Description, int Goal, int Progress, int RewardCredits, bool Claimed);

    // Increment a quest's progress for today. No-op-safe to call for any code from gameplay hooks.
    public static async Task AddProgressAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId, string code, int amount)
    {
        if (amount <= 0) return;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_daily_quests (player_id, quest_code, quest_date, progress, claimed) VALUES ({0}, {1}, CURDATE(), {2}, 0) " +
            "ON DUPLICATE KEY UPDATE progress = progress + VALUES(progress)", playerId, code, amount);
    }

    public static async Task<List<QuestView>> GetQuestsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var result = new List<QuestView>();
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dq.code, dq.name, dq.description, dq.goal, COALESCE(pdq.progress,0), dq.reward_credits, COALESCE(pdq.claimed,0) " +
                "FROM daily_quests dq LEFT JOIN player_daily_quests pdq ON pdq.quest_code = dq.code AND pdq.player_id = @p AND pdq.quest_date = CURDATE() " +
                "WHERE dq.enabled = 1 ORDER BY dq.sort_order, dq.id";
            var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = playerId; cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new QuestView(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6) == 1));
            }
        }
        finally { await conn.CloseAsync(); }
        return result;
    }

    // Claim a completed quest's reward (once). Returns (ok, rewardCredits).
    public static async Task<(bool Ok, int Reward)> ClaimAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic player, string code)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        int goal, reward, progress, claimed;
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dq.goal, dq.reward_credits, COALESCE(pdq.progress,0), COALESCE(pdq.claimed,0) " +
                "FROM daily_quests dq LEFT JOIN player_daily_quests pdq ON pdq.quest_code = dq.code AND pdq.player_id = @p AND pdq.quest_date = CURDATE() " +
                "WHERE dq.code = @c AND dq.enabled = 1";
            var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = player.Id; cmd.Parameters.Add(p);
            var c = cmd.CreateParameter(); c.ParameterName = "@c"; c.Value = code; cmd.Parameters.Add(c);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return (false, 0);
            goal = reader.GetInt32(0); reward = reader.GetInt32(1); progress = reader.GetInt32(2); claimed = reader.GetInt32(3);
        }
        finally { await conn.CloseAsync(); }

        if (claimed == 1 || progress < goal) return (false, 0);

        // Atomically claim (only one wins) — guards against double-claim.
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_daily_quests SET claimed = 1 WHERE player_id = {0} AND quest_code = {1} AND quest_date = CURDATE() AND claimed = 0",
            player.Id, code);
        if (affected == 0) return (false, 0);

        await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, player.Id, reward);
        return (true, reward);
    }
}
