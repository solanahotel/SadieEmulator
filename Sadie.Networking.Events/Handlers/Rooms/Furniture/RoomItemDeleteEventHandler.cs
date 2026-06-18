using System.Drawing;
using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Enums.Game.Furniture;
using Sadie.Enums.Game.Players;
using Sadie.Networking.Writers.Players.Inventory;
using Sadie.Networking.Writers.Rooms.Furniture;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Rooms.Furniture;

// Custom (non-Habbo) packet: permanently delete a placed furniture item from the
// game. Unlike RoomItemEjected (which returns the item to the owner's inventory),
// this destroys the player_furniture_items row entirely. Gated on the
// any_room_rights permission so only staff/admins can use it.
[PacketId(EventHandlerId.RoomItemDelete)]
public class RoomItemDeleteEventHandler(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IRoomRepository roomRepository,
    IPlayerRepository playerRepository,
    IRoomTileMapHelperService tileMapHelperService) : INetworkPacketEventHandler
{
    public int ItemId { get; init; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || client.RoomUser == null)
        {
            return;
        }

        if (!client.Player.HasPermission(PlayerPermissionName.AnyRoomRights))
        {
            return;
        }

        var room = roomRepository.TryGetRoomById(client.Player.State.CurrentRoomId);

        var roomFurnitureItem = room?
            .FurnitureItems
            .FirstOrDefault(x => x.PlayerFurnitureItemId == ItemId);

        if (room == null || roomFurnitureItem == null)
        {
            return;
        }

        var ownerId = roomFurnitureItem.PlayerFurnitureItem.PlayerId;

        if (roomFurnitureItem.FurnitureItem.Type == FurnitureItemType.Floor)
        {
            await room.UserRepository.BroadcastDataAsync(new RoomFloorFurnitureItemRemovedWriter
            {
                Id = roomFurnitureItem.PlayerFurnitureItemId.ToString(),
                Expired = false,
                OwnerId = ownerId,
                Delay = 0
            });
        }
        else
        {
            await room.UserRepository.BroadcastDataAsync(new Sadie.Networking.Events.Writers.FixedRoomWallFurnitureItemRemovedWriter
            {
                Item = roomFurnitureItem
            });
        }

        room.FurnitureItems.Remove(roomFurnitureItem);

        var point = new Point(roomFurnitureItem.PositionX, roomFurnitureItem.PositionY);

        foreach (var user in tileMapHelperService.GetUsersAtPoints([point], room.UserRepository.GetAll()))
        {
            user.CheckStatusForCurrentTile();
        }

        // Permanently delete the item; FK cascade removes placement_data + links.
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "DELETE FROM player_furniture_items WHERE id = {0}", ItemId);
        }

        // Keep the owner's in-memory inventory in sync if they're online.
        var owner = playerRepository.GetPlayerLogicById(ownerId);

        if (owner is { NetworkObject: not null })
        {
            var inventoryItem = owner.FurnitureItems.FirstOrDefault(x => x.Id == ItemId);

            if (inventoryItem != null)
            {
                owner.FurnitureItems.Remove(inventoryItem);
            }

            await owner.NetworkObject.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
        }
    }
}
