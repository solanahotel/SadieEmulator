using Microsoft.EntityFrameworkCore;
using Sadie.Db;

namespace Sadie.Networking.Events.Effects;

// One owned avatar effect (a row in player_effects joined to the effects catalog).
public sealed record OwnedEffect(int EffectId, string Name, int Quantity, bool IsActive);

// Data access for the effects inventory: ownership, the active effect, activate/deactivate, and grant.
// Effects are owned permanently (activation toggles, it does NOT consume quantity).
public static class EffectService
{
    public static async Task<List<OwnedEffect>> GetInventoryAsync(IDbContextFactory<SadieDbContext> factory, long playerId)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT pe.effect_id, COALESCE(e.name, CONCAT('Effect #', pe.effect_id)), pe.quantity, pe.is_active " +
                "FROM player_effects pe LEFT JOIN effects e ON e.id = pe.effect_id " +
                "WHERE pe.player_id = @p AND pe.quantity > 0 ORDER BY e.name";
            var p = command.CreateParameter();
            p.ParameterName = "@p";
            p.Value = playerId;
            command.Parameters.Add(p);

            var list = new List<OwnedEffect>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(new OwnedEffect(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3) == 1));
            return list;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public static async Task<bool> OwnsAsync(IDbContextFactory<SadieDbContext> factory, long playerId, int effectId)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        return (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM player_effects WHERE player_id = {0} AND effect_id = {1} AND quantity > 0",
            playerId, effectId).ToListAsync()).FirstOrDefault() > 0;
    }

    public static async Task<int> GetActiveAsync(IDbContextFactory<SadieDbContext> factory, long playerId)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        return (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT effect_id AS Value FROM player_effects WHERE player_id = {0} AND is_active = 1 AND quantity > 0 LIMIT 1",
            playerId).ToListAsync()).FirstOrDefault();
    }

    public static async Task ActivateAsync(IDbContextFactory<SadieDbContext> factory, long playerId, int effectId)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE player_effects SET is_active = 0 WHERE player_id = {0}", playerId);
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_effects SET is_active = 1 WHERE player_id = {0} AND effect_id = {1}", playerId, effectId);
    }

    public static async Task DeactivateAsync(IDbContextFactory<SadieDbContext> factory, long playerId)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE player_effects SET is_active = 0 WHERE player_id = {0}", playerId);
    }

    // Grant an effect from the catalog (admin). Returns false if the effect id isn't a known effect.
    public static async Task<bool> GrantAsync(IDbContextFactory<SadieDbContext> factory, long playerId, int effectId, int quantity)
    {
        await using var dbContext = await factory.CreateDbContextAsync();
        var known = (await dbContext.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM effects WHERE id = {0}", effectId).ToListAsync()).FirstOrDefault() > 0;
        if (!known) return false;

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_effects (player_id, effect_id, quantity, created_at) VALUES ({0}, {1}, {2}, NOW()) " +
            "ON DUPLICATE KEY UPDATE quantity = quantity + {2}", playerId, effectId, quantity);
        return true;
    }
}
