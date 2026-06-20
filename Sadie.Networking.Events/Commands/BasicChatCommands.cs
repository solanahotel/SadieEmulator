using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Users;
using Sadie.Enums.Game.Rooms.Users;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Rooms.Users;

namespace Sadie.Networking.Events.Commands;

// ---- Everyone-tier chat commands (no permission required) ----

public class SitChatCommand : IRoomChatCommand
{
    public string Trigger => "sit";
    public string Description => "Sit down";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        user.RemoveStatuses(RoomUserStatus.Lay);
        user.AddStatus(RoomUserStatus.Sit, "0.5");
        await user.Room.UserRepository.SendUserStatusUpdatesAsync();
    }
}

public class StandChatCommand : IRoomChatCommand
{
    public string Trigger => "stand";
    public string Description => "Stand up";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        user.RemoveStatuses(RoomUserStatus.Sit, RoomUserStatus.Lay);
        await user.Room.UserRepository.SendUserStatusUpdatesAsync();
    }
}

public class LayChatCommand : IRoomChatCommand
{
    public string Trigger => "lay";
    public string Description => "Lie down";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        user.RemoveStatuses(RoomUserStatus.Sit);
        user.AddStatus(RoomUserStatus.Lay, "0.5");
        await user.Room.UserRepository.SendUserStatusUpdatesAsync();
    }
}

public class EnableChatCommand : IRoomChatCommand
{
    public string Trigger => "enable";
    public string Description => "Give yourself an avatar effect";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => false;
    public List<string> Parameters => ["effect id"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetInt(out var effectId))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :enable [effect id]" });
            return;
        }

        // Setting ActiveEffectId (not a tile effect) makes it survive walking — RoomUser preserves it.
        user.ActiveEffectId = effectId;

        await user.Room.UserRepository.BroadcastDataAsync(new RoomUserEffectWriter
        {
            UserId = (int) user.Player.Id,
            EffectId = effectId,
            DelayMs = 0
        });
    }
}
