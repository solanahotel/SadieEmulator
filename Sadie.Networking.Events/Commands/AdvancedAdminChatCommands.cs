using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db;
using Sadie.Enums.Game.Players;
using Sadie.Networking.Events.Effects;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Rooms.Users;

namespace Sadie.Networking.Events.Commands;

// ---- More admin commands (require "admin") ----

public class GiveBadgeChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "givebadge";
    public string Description => "Give a user a badge";
    public List<string> PermissionsRequired { get; set; } = ["admin"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "badge code"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetWord(out var code) || string.IsNullOrWhiteSpace(code))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :givebadge [username] [badge code]" });
            return;
        }

        var targetId = playerRepository.GetPlayerLogicByUsername(username!)?.Id
                       ?? (await playerRepository.GetPlayerByUsernameAsync(username!))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO badges (code) SELECT {0} FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM badges WHERE code = {0})", code);

        var badgeIds = await dbContext.Database
            .SqlQueryRaw<int>("SELECT id AS Value FROM badges WHERE code = {0}", code!).ToListAsync();

        if (badgeIds.Count == 0)
        {
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO player_badges (player_id, badge_id, slot) SELECT {0}, {1}, 0 FROM DUAL " +
            "WHERE NOT EXISTS (SELECT 1 FROM player_badges WHERE player_id = {0} AND badge_id = {1})",
            targetId.Value, badgeIds[0]);

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Gave badge '{code}' to {username}. They must relog to see it." });
    }
}

public class MimicChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "mimic";
    public string Description => "Copy another user's look";
    public List<string> PermissionsRequired { get; set; } = ["admin"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :mimic [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);
        if (target?.AvatarData == null || user.Player.AvatarData == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        var figure = target.AvatarData.FigureCode;
        user.Player.AvatarData.FigureCode = figure;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE player_avatar_data SET figure_code = {0} WHERE player_id = {1}", figure, user.Player.Id);

        var genderCode = user.Player.AvatarData.Gender == PlayerAvatarGender.Male ? "M" : "F";

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerChangedAppearanceWriter { FigureCode = figure, Gender = genderCode });
        await user.Room.UserRepository.BroadcastDataAsync(new RoomUserDataWriter { Users = [user] });
    }
}

// Grant an avatar effect (from the effects catalog) to a player's Effects inventory.
public class GiveEffectChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "giveeffect";
    public string Description => "Give a user an avatar effect they can equip from Effects";
    public List<string> PermissionsRequired { get; set; } = ["admin"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "effect id", "quantity (optional)"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetInt(out var effectId))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :giveeffect [username] [effect id] [quantity]" });
            return;
        }

        var quantity = reader.GetInt(out var q) && q > 0 ? q : 1;

        var targetId = playerRepository.GetPlayerLogicByUsername(username!)?.Id
                       ?? (await playerRepository.GetPlayerByUsernameAsync(username!))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        var granted = await EffectService.GrantAsync(dbContextFactory, targetId.Value, effectId, quantity);

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter
        {
            Message = granted
                ? $"Gave effect #{effectId} x{quantity} to {username}. They can equip it from Effects (reopen the window)."
                : $"Effect #{effectId} is not a known effect."
        });
    }
}
