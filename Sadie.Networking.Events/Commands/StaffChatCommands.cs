using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Users;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Rooms.Users;

namespace Sadie.Networking.Events.Commands;

// ---- Staff / Moderator chat commands (require the "moderator" permission) ----

internal static class StaffCommandHelpers
{
    public const string Perm = "moderator";
}

public class KickChatCommand(IPlayerRepository playerRepository, IRoomRepository roomRepository) : IRoomChatCommand
{
    public string Trigger => "kick";
    public string Description => "Kick a user out of their room";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :kick [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);
        var room = target == null ? null : roomRepository.TryGetRoomById(target.State.CurrentRoomId);

        if (target == null || room == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not in a room." });
            return;
        }

        await room.UserRepository.TryRemoveAsync(target.Id, notifyLeft: true, hotelView: true);
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Kicked {target.Username}." });
    }
}

public class AlertChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "alert";
    public string Description => "Send an alert to one user";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username", "message"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || !reader.GetSentence(out var message) || string.IsNullOrWhiteSpace(message))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :alert [username] [message]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username!);

        if (target?.NetworkObject == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        await target.NetworkObject.WriteToStreamAsync(new PlayerAlertWriter { Message = message! });
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Alert sent to {target.Username}." });
    }
}

public class HotelAlertChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "ha";
    public string Description => "Alert every user online";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["message"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetSentence(out var message) || string.IsNullOrWhiteSpace(message))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :ha [message]" });
            return;
        }

        await playerRepository.BroadcastDataAsync(new PlayerAlertWriter { Message = message! });
    }
}

public class UserInfoChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "userinfo";
    public string Description => "Look up a user";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :userinfo [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);

        if (target == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter
        {
            Message = $"User: {target.Username}\nId: {target.Id}\nCredits: {target.Data.CreditBalance}\nPixels: {target.Data.PixelBalance}\nRoom: {target.State.CurrentRoomId}"
        });
    }
}

public class SummonChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "summon";
    public string Description => "Summon a user to your room";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :summon [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);

        if (target?.NetworkObject == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        await target.NetworkObject.WriteToStreamAsync(new RoomForwardEntryWriter { RoomId = user.Player.State.CurrentRoomId });
    }
}

public class TeleportChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "teleport";
    public string Description => "Teleport yourself to a user";
    public List<string> PermissionsRequired { get; set; } = [StaffCommandHelpers.Perm];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :teleport [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);

        if (target == null || target.State.CurrentRoomId == 0)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not in a room." });
            return;
        }

        await user.Player.NetworkObject!.WriteToStreamAsync(new RoomForwardEntryWriter { RoomId = target.State.CurrentRoomId });
    }
}
