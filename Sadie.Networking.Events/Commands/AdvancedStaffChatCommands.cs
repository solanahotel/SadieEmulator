using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Rooms.Users;

namespace Sadie.Networking.Events.Commands;

// ---- More staff/moderator commands (require "moderator") ----

public class FreezeChatCommand(IPlayerRepository playerRepository, IRoomRepository roomRepository) : IRoomChatCommand
{
    public string Trigger => "freeze";
    public string Description => "Stop a user from walking";
    public List<string> PermissionsRequired { get; set; } = ["moderator"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        var targetRoomUser = ResolveRoomUser(reader, playerRepository, roomRepository, out var name);
        if (targetRoomUser == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{name} is not in a room." });
            return;
        }

        targetRoomUser.CanWalk = false;
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Froze {targetRoomUser.Player.Username}." });
    }

    internal static IRoomUser? ResolveRoomUser(IRoomChatCommandParameterReader reader, IPlayerRepository playerRepository, IRoomRepository roomRepository, out string name)
    {
        name = reader.GetWord(out var n) ? n ?? "" : "";
        var target = playerRepository.GetPlayerLogicByUsername(name);
        var room = target == null ? null : roomRepository.TryGetRoomById(target.State.CurrentRoomId);
        if (room != null && room.UserRepository.TryGetById(target!.Id, out var roomUser))
        {
            return roomUser;
        }
        return null;
    }
}

public class UnfreezeChatCommand(IPlayerRepository playerRepository, IRoomRepository roomRepository) : IRoomChatCommand
{
    public string Trigger => "unfreeze";
    public string Description => "Let a user walk again";
    public List<string> PermissionsRequired { get; set; } = ["moderator"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        var targetRoomUser = FreezeChatCommand.ResolveRoomUser(reader, playerRepository, roomRepository, out var name);
        if (targetRoomUser == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{name} is not in a room." });
            return;
        }

        targetRoomUser.CanWalk = true;
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Unfroze {targetRoomUser.Player.Username}." });
    }
}

public class InvisibleChatCommand : IRoomChatCommand
{
    public string Trigger => "invisible";
    public string Description => "Toggle hiding yourself from others in the room";
    public List<string> PermissionsRequired { get; set; } = ["moderator"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        var nowInvisible = !InvisibleStore.IsInvisible(user.Player.Id);
        InvisibleStore.SetInvisible(user.Player.Id, nowInvisible);

        if (nowInvisible)
        {
            // Remove your avatar for everyone else (you still see yourself).
            await user.Room.UserRepository.BroadcastDataAsync(
                new RoomUserLeftWriter { UserId = user.Player.Id.ToString() }, [user.Player.Id]);
        }
        else
        {
            await user.Room.UserRepository.BroadcastDataAsync(new RoomUserDataWriter { Users = [user] });
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter
        {
            Message = nowInvisible ? "You are now invisible to others in this room." : "You are visible again."
        });
    }
}

public class BanChatCommand(
    IPlayerRepository playerRepository,
    IRoomRepository roomRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "ban";
    public string Description => "Ban a user (optional hours, else permanent)";
    public List<string> PermissionsRequired { get; set; } = ["moderator"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "hours"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :ban [username] [hours]" });
            return;
        }

        var online = playerRepository.GetPlayerLogicByUsername(username);
        var targetId = online?.Id ?? (await playerRepository.GetPlayerByUsernameAsync(username))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        var now = DateTime.Now;
        var hasHours = reader.GetInt(out var hours) && hours > 0;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        if (hasHours)
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_bans (creator_id, player_id, reason, created_at, expires_at) VALUES ({0}, {1}, {2}, {3}, {4})",
                user.Player.Id, targetId.Value, "Banned by staff", now, now.AddHours(hours));
        }
        else
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_bans (creator_id, player_id, reason, created_at, expires_at) VALUES ({0}, {1}, {2}, {3}, NULL)",
                user.Player.Id, targetId.Value, "Banned by staff", now);
        }

        // If online, boot them out now; the login check blocks re-entry.
        if (online != null)
        {
            var room = roomRepository.TryGetRoomById(online.State.CurrentRoomId);
            if (room != null)
            {
                await room.UserRepository.TryRemoveAsync(online.Id, notifyLeft: true, hotelView: true);
            }
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter
        {
            Message = hasHours ? $"Banned {username} for {hours} hour(s)." : $"Permanently banned {username}."
        });
    }
}

public class UnbanChatCommand(
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "unban";
    public string Description => "Remove a user's ban";
    public List<string> PermissionsRequired { get; set; } = ["moderator"];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :unban [username]" });
            return;
        }

        var targetId = playerRepository.GetPlayerLogicByUsername(username)?.Id
                       ?? (await playerRepository.GetPlayerByUsernameAsync(username))?.Id;

        if (targetId == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} not found." });
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM player_bans WHERE player_id = {0}", targetId.Value);

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Unbanned {username}." });
    }
}
