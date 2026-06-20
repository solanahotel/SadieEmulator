using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.Db;
using Sadie.Networking.Writers.Players.Purse;

namespace Sadie.Networking.Events.Economy;

// Central credit give/take used by the marketplace (and later faucets, wheel, sinks). Keeps the
// in-memory balance of online players in sync with player_data and pushes a live balance update to
// the client. Mirrors the proven :credits command pattern. credit_balance is settable on PlayerData
// (unlike the init-only achievement score), so the in-memory value stays authoritative.
public static class CurrencyService
{
    // Add credits to a player (online or offline). No-op for non-positive amounts.
    public static async Task GiveCreditsAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        long playerId, int amount)
    {
        if (amount <= 0) return;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_data SET credit_balance = credit_balance + {0} WHERE player_id = {1}", amount, playerId);

        await SyncOnlineAsync(playerRepository, dbContext, playerId);
    }

    // Atomically remove credits. Returns false and moves nothing if the player can't afford it (the
    // conditional UPDATE makes this race-safe even under concurrent spends).
    public static async Task<bool> TakeCreditsAsync(
        IPlayerRepository playerRepository,
        IDbContextFactory<SadieDbContext> dbContextFactory,
        long playerId, int amount)
    {
        if (amount <= 0) return true;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_data SET credit_balance = credit_balance - {0} WHERE player_id = {1} AND credit_balance >= {0}",
            amount, playerId);

        if (affected == 0) return false;

        await SyncOnlineAsync(playerRepository, dbContext, playerId);
        return true;
    }

    public static async Task<int> GetCreditsAsync(
        IDbContextFactory<SadieDbContext> dbContextFactory, long playerId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return await ReadBalanceAsync(dbContext, playerId);
    }

    // Re-read the authoritative balance; if the player is online, sync their in-memory value and push
    // a live balance refresh to the client.
    private static async Task SyncOnlineAsync(
        IPlayerRepository playerRepository, SadieDbContext dbContext, long playerId)
    {
        var online = playerRepository.GetPlayerLogicById(playerId);
        if (online?.Data == null) return;

        var balance = await ReadBalanceAsync(dbContext, playerId);
        online.Data.CreditBalance = balance;

        if (online.NetworkObject != null)
        {
            await online.NetworkObject.WriteToStreamAsync(new PlayerCreditsBalanceWriter { Credits = balance });
        }
    }

    private static async Task<int> ReadBalanceAsync(SadieDbContext dbContext, long playerId) =>
        (await dbContext.Database
            .SqlQueryRaw<int>("SELECT credit_balance AS Value FROM player_data WHERE player_id = {0}", playerId)
            .ToListAsync()).FirstOrDefault();
}
