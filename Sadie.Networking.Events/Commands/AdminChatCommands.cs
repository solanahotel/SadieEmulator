using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Players.Purse;

namespace Sadie.Networking.Events.Commands;

// ---- Admin chat commands (require the "admin" permission) ----

internal static class AdminCommandHelpers
{
    public const string Perm = "admin";
}

public class CreditsChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "credits";
    public string Description => "Give a user credits";
    public List<string> PermissionsRequired { get; set; } = [AdminCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "amount"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetInt(out var amount) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :credits [username] [amount]" });
            return;
        }

        var online = playerRepository.GetPlayerLogicByUsername(username);
        var targetId = online?.Id ?? (await playerRepository.GetPlayerByUsernameAsync(username))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        if (online != null)
        {
            // Update in-memory + DB so it sticks even if their session later saves.
            online.Data.CreditBalance += amount;
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_data SET credit_balance = {0} WHERE player_id = {1}", online.Data.CreditBalance, targetId.Value);

            if (online.NetworkObject != null)
            {
                await online.NetworkObject.WriteToStreamAsync(new PlayerCreditsBalanceWriter { Credits = online.Data.CreditBalance });
            }
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_data SET credit_balance = credit_balance + {0} WHERE player_id = {1}", amount, targetId.Value);
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Gave {amount} credits to {username}." });
    }
}

public class ClubChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "club";
    public string Description => "Grant Solana Club to a user";
    public List<string> PermissionsRequired { get; set; } = [AdminCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "days"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetInt(out var days) || days <= 0)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :club [username] [days]" });
            return;
        }

        var online = playerRepository.GetPlayerLogicByUsername(username!);
        var targetId = online?.Id ?? (await playerRepository.GetPlayerByUsernameAsync(username!))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var subscription = await dbContext.Subscriptions.FirstOrDefaultAsync(x => x.Name == "HABBO_CLUB");
        if (subscription == null)
        {
            return;
        }

        var existing = await dbContext.PlayerSubscriptions
            .FirstOrDefaultAsync(x => x.PlayerId == targetId.Value && x.SubscriptionId == subscription.Id);

        var now = DateTime.Now;

        if (existing != null)
        {
            var baseTime = existing.ExpiresAt.DateTime > now ? existing.ExpiresAt.DateTime : now;
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE player_subscriptions SET expires_at = {0} WHERE id = {1}",
                baseTime.AddDays(days), existing.Id);
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_subscriptions (player_id, subscription_id, created_at, expires_at) VALUES ({0}, {1}, {2}, {3})",
                targetId.Value, subscription.Id, now, now.AddDays(days));
        }

        if (online?.NetworkObject != null)
        {
            await online.NetworkObject.WriteToStreamAsync(new PlayerAlertWriter { Message = $"You received {days} day(s) of Solana Club! Relog to apply." });
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Granted {days} day(s) of Solana Club to {username}." });
    }
}

public class SetRankChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "setrank";
    public string Description => "Set a user's rank (1=User, 5=Moderator, 6=Admin)";
    public List<string> PermissionsRequired { get; set; } = [AdminCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "roleId"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetInt(out var roleId))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :setrank [username] [1=User, 5=Moderator, 6=Admin]" });
            return;
        }

        if (roleId != 1 && roleId != 5 && roleId != 6)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Valid roles: 1=User, 5=Moderator, 6=Admin." });
            return;
        }

        var online = playerRepository.GetPlayerLogicByUsername(username!);
        var targetId = online?.Id ?? (await playerRepository.GetPlayerByUsernameAsync(username!))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM player_role WHERE player_id = {0}", targetId.Value);
        await dbContext.Database.ExecuteSqlRawAsync("INSERT INTO player_role (player_id, role_id) VALUES ({0}, {1})", targetId.Value, roleId);

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Set {username} to role {roleId}. They must relogin to apply." });
    }
}
