using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;

namespace Sadie.Networking.Events.Economy;

// Credit faucets: capped daily, reset every 24h, ×1.5 caps for members. Every grant is metered against
// player_faucet_earnings (per faucet, per day) so a player can never exceed a faucet's daily cap.
public static class FaucetService
{
    // base earning, base daily cap (member caps are ×1.5)
    public const int LoginAmount = 50, LoginCap = 50;
    public const int StreakStep = 10, StreakCap = 50, StreakMaxDays = 5;
    public const int VisitorAmount = 4, VisitorCap = 20;   // room owner, per unique visitor/day
    public const int VisitAmount = 4, VisitCap = 20;        // visitor, per unique room/day
    public const int ActiveAmount = 10, ActiveCap = 50;     // 10 per 30 min active, max 5/day (2.5h)
    public const int StartingGrant = 100;                   // one-time new-player grant

    // Grant up to the faucet's remaining daily cap. Returns the amount actually credited.
    public static async Task<int> GrantAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        long playerId, string faucet, int rawAmount, int baseCap, bool isMember)
    {
        if (rawAmount <= 0) return 0;

        var multiplier = isMember ? 1.5 : 1.0;
        var cap = (int) Math.Floor(baseCap * multiplier);
        var want = (int) Math.Floor(rawAmount * multiplier);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var earned = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COALESCE(SUM(amount),0) AS Value FROM player_faucet_earnings WHERE player_id = {0} AND faucet = {1} AND earned_on = CURDATE()",
            playerId, faucet).ToListAsync()).FirstOrDefault();

        var grant = Math.Min(want, cap - earned);
        if (grant <= 0) return 0;

        await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, playerId, grant);
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_faucet_earnings (player_id, faucet, earned_on, amount) VALUES ({0}, {1}, CURDATE(), {2}) " +
            "ON DUPLICATE KEY UPDATE amount = amount + VALUES(amount)", playerId, faucet, grant);
        return grant;
    }

    // True the first time (player, kind, refId) is seen today — used to count unique visitors / rooms.
    public static async Task<bool> TryClaimUniqueAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId, string kind, long refId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT IGNORE INTO player_faucet_unique (player_id, kind, ref_id, logged_on) VALUES ({0}, {1}, {2}, CURDATE())",
            playerId, kind, refId);
        return affected > 0;
    }

    public static async Task<bool> IsMemberAsync(IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM player_subscriptions WHERE player_id = {0} AND expires_at > {1}",
            playerId, DateTime.Now).ToListAsync()).FirstOrDefault() > 0;
    }

    // One-time 100-credit starting grant (resists farming: given on first qualifying login, not bare
    // registration). When the $HOTEL access gate lands this can require a wallet meeting the gate.
    public static async Task<int> GrantNewPlayerAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory, IPlayerLogic player)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var already = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM player_faucet_earnings WHERE player_id = {0} AND faucet = 'starting_grant'",
            player.Id).ToListAsync()).FirstOrDefault();
        if (already > 0) return 0;

        await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, player.Id, StartingGrant);
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_faucet_earnings (player_id, faucet, earned_on, amount) VALUES ({0}, 'starting_grant', CURDATE(), {1}) " +
            "ON DUPLICATE KEY UPDATE amount = amount", player.Id, StartingGrant);
        return StartingGrant;
    }

    public sealed record DailyResult(bool FirstToday, int Total, int Streak, int LoginCredits, int StreakCredits);

    // Daily-login (50) + streak bonus (10/day, climbing to 50 on a 5-day streak). Once per day. The
    // streak resets to 1 whenever the previous login wasn't yesterday (i.e. a day was missed).
    public static async Task<DailyResult> GrantDailyAndStreakAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic player, bool isMember)
    {
        DateTime? lastLogin;
        var streakDays = 0;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            var conn = dbContext.Database.GetDbConnection();
            await conn.OpenAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT last_login_date, streak_days FROM player_login_streaks WHERE player_id = @p";
                var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = player.Id; cmd.Parameters.Add(p);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    lastLogin = reader.GetDateTime(0);
                    streakDays = reader.GetInt32(1);
                }
                else lastLogin = null;
            }
            finally { await conn.CloseAsync(); }
        }

        var today = DateTime.Today;
        if (lastLogin.HasValue && lastLogin.Value.Date == today)
            return new DailyResult(false, 0, streakDays, 0, 0); // already processed today

        // Continue the streak only if the last login was yesterday; otherwise it's broken -> back to 1.
        int streak;
        if (lastLogin.HasValue && lastLogin.Value.Date == today.AddDays(-1)) streak = Math.Min(streakDays + 1, StreakMaxDays);
        else streak = 1;

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_login_streaks (player_id, last_login_date, streak_days) VALUES ({0}, CURDATE(), {1}) " +
                "ON DUPLICATE KEY UPDATE last_login_date = VALUES(last_login_date), streak_days = VALUES(streak_days)",
                player.Id, streak);
        }

        var login = await GrantAsync(playerRepository, dbContextFactory, player.Id, "daily_login", LoginAmount, LoginCap, isMember);
        var streakBonus = await GrantAsync(playerRepository, dbContextFactory, player.Id, "streak", streak * StreakStep, StreakCap, isMember);
        return new DailyResult(true, login + streakBonus, streak, login, streakBonus);
    }
}
