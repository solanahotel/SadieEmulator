using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;

namespace Sadie.Networking.Events.Economy;

// Daily Wheel (one free spin/day, separate from faucet caps). Weighted credit tiers + a Rare Item Box,
// with better member odds. The Rare Item Box pulls a random marketable item of a rolled rarity.
public static class WheelService
{
    public sealed record SpinResult(bool Ok, string Error, string RewardType, int Amount, string Label);
    public sealed record WheelState(bool CanSpin, string LastReward);

    public static async Task<WheelState> GetStateAsync(IDbContextFactory<SadieDbContext> dbContextFactory, long playerId, bool isAdmin = false)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var label = await ReadTodayLabelAsync(dbContext, playerId);
        // Admins can always spin (unlimited).
        return new WheelState(isAdmin || label == null, label ?? "");
    }

    public static async Task<SpinResult> SpinAsync(
        IPlayerRepository playerRepository, IDbContextFactory<SadieDbContext> dbContextFactory,
        IPlayerLogic player, bool isMember, bool isAdmin = false)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        if (!isAdmin && await ReadTodayLabelAsync(dbContext, player.Id) != null)
            return new SpinResult(false, "You've already spun today — come back tomorrow!", "", 0, "");

        // Cumulative thresholds (regular / member). roll in [0,100).
        var box = isMember ? 95 : 97;
        var t100 = isMember ? 78 : 85;
        var t50 = isMember ? 53 : 65;
        var t25 = isMember ? 25 : 35;

        var roll = Random.Shared.Next(100);
        string type;
        int amount;
        string label;

        if (roll >= box)
        {
            // Award a (non-tradeable) Rare Box present; it opens into one of the configured pool items.
            type = "rare_item"; amount = 0; label = "a Rare Box";
            await MarketplaceService.GrantRareBoxAsync(playerRepository, dbContextFactory, player.Id);
        }
        else
        {
            amount = roll >= t100 ? 100 : roll >= t50 ? 50 : roll >= t25 ? 25 : 10;
            type = "credits"; label = $"{amount} credits";
            await CurrencyService.GiveCreditsAsync(playerRepository, dbContextFactory, player.Id, amount);
        }

        // ON DUPLICATE KEY UPDATE keeps the daily UNIQUE intact while letting admins re-spin (overwrites today's row).
        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_wheel_spins (player_id, spun_on, reward_type, reward_amount, reward_label, is_member, created_at) VALUES ({0}, CURDATE(), {1}, {2}, {3}, {4}, {5}) " +
            "ON DUPLICATE KEY UPDATE reward_type = VALUES(reward_type), reward_amount = VALUES(reward_amount), reward_label = VALUES(reward_label), is_member = VALUES(is_member), created_at = VALUES(created_at)",
            player.Id, type, amount, label, isMember ? 1 : 0, DateTime.Now);

        return new SpinResult(true, "", type, amount, label);
    }

    private static async Task<string?> ReadTodayLabelAsync(SadieDbContext dbContext, long playerId)
    {
        var conn = dbContext.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT reward_label FROM player_wheel_spins WHERE player_id = @p AND spun_on = CURDATE() LIMIT 1";
            var p = cmd.CreateParameter(); p.ParameterName = "@p"; p.Value = playerId; cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync();
            return result is null or DBNull ? null : (string) result;
        }
        finally { await conn.CloseAsync(); }
    }

}
