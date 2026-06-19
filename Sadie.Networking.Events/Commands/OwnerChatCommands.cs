using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Enums.Game.Furniture;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Players.Inventory;
using Sadie.Networking.Writers.Rooms.Furniture;

namespace Sadie.Networking.Events.Commands;

// ---- Room-owner chat commands (BypassPermissionCheckIfRoomOwner => owner only) ----

internal static class FurnitureEjectHelper
{
    public static async Task EjectAsync(
        IRoomUser user,
        List<PlayerFurnitureItemPlacementData> items,
        IRoomTileMapHelperService tileMapHelperService,
        IDbContextFactory<SadieDbContext> dbContextFactory)
    {
        var room = user.Room;

        if (items.Count == 0)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "No furniture to pick up." });
            return;
        }

        foreach (var item in items)
        {
            await room.UserRepository.BroadcastDataAsync(new RoomFloorFurnitureItemRemovedWriter
            {
                Id = item.PlayerFurnitureItemId.ToString(),
                Expired = false,
                OwnerId = 0,
                Delay = 0
            });

            var points = tileMapHelperService.GetPointsForPlacement(
                item.PositionX, item.PositionY, item.FurnitureItem.TileSpanX, item.FurnitureItem.TileSpanY, (int) item.Direction);

            room.FurnitureItems.Remove(item);
            tileMapHelperService.UpdateTileMapsForPoints(points, room.TileMap, room.FurnitureItems);

            // PlacementData = null makes the item show in its owner's inventory again.
            if (item.PlayerFurnitureItem != null)
            {
                item.PlayerFurnitureItem.PlacementData = null;
            }
        }

        // Delete the placement rows directly — robust across DbContexts (the items were
        // loaded by the room's context; EF attach+delete in a loop was failing).
        var placementIds = string.Join(",", items.Select(x => x.Id));
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Database.ExecuteSqlRawAsync(
            $"DELETE FROM player_furniture_item_placement_data WHERE id IN ({placementIds})");
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Picked up {items.Count} item(s)." });
    }
}

public class PickAllChatCommand(
    IRoomTileMapHelperService tileMapHelperService,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "pickall";
    public string Description => "Pick up all of your furniture in this room";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => true;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        var items = user.Room.FurnitureItems
            .Where(x => x.FurnitureItem.Type == FurnitureItemType.Floor && x.PlayerFurnitureItem?.PlayerId == user.Player.Id)
            .ToList();

        await FurnitureEjectHelper.EjectAsync(user, items, tileMapHelperService, dbContextFactory);
    }
}

public class EjectAllChatCommand(
    IRoomTileMapHelperService tileMapHelperService,
    IDbContextFactory<SadieDbContext> dbContextFactory) : IRoomChatCommand
{
    public string Trigger => "ejectall";
    public string Description => "Send all furniture in this room back to inventory";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => true;
    public List<string> Parameters => [];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        var items = user.Room.FurnitureItems
            .Where(x => x.FurnitureItem.Type == FurnitureItemType.Floor)
            .ToList();

        await FurnitureEjectHelper.EjectAsync(user, items, tileMapHelperService, dbContextFactory);
    }
}

public class MuteChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "mute";
    public string Description => "Mute a user in this room";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => true;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :mute [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);
        if (target == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        ChatMuteStore.MutePlayer(user.Room.Id, target.Id);
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Muted {target.Username} in this room." });
    }
}

public class UnmuteChatCommand(IPlayerRepository playerRepository) : IRoomChatCommand
{
    public string Trigger => "unmute";
    public string Description => "Unmute a user in this room";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => true;
    public List<string> Parameters => ["username"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        if (!reader.GetWord(out var username) || string.IsNullOrWhiteSpace(username))
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :unmute [username]" });
            return;
        }

        var target = playerRepository.GetPlayerLogicByUsername(username);
        if (target == null)
        {
            await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"{username} is not online." });
            return;
        }

        ChatMuteStore.UnmutePlayer(user.Room.Id, target.Id);
        await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = $"Unmuted {target.Username}." });
    }
}

public class RoomControlChatCommand : IRoomChatCommand
{
    public string Trigger => "room";
    public string Description => "Mute or unmute the whole room";
    public List<string> PermissionsRequired { get; set; } = [];
    public bool BypassPermissionCheckIfRoomOwner => true;
    public List<string> Parameters => ["mute|unmute"];

    public async Task ExecuteAsync(IRoomUser user, IRoomChatCommandParameterReader reader)
    {
        reader.GetWord(out var sub);

        switch ((sub ?? "").ToLower())
        {
            case "mute":
                ChatMuteStore.MuteRoom(user.Room.Id);
                await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "This room is now muted." });
                break;
            case "unmute":
                ChatMuteStore.UnmuteRoom(user.Room.Id);
                await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "This room is no longer muted." });
                break;
            default:
                await user.Player.NetworkObject!.WriteToStreamAsync(new PlayerAlertWriter { Message = "Usage: :room [mute|unmute]" });
                break;
        }
    }
}
