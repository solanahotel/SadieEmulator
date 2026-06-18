using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Furniture;
using Sadie.API.Networking;
using Sadie.Db;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Enums.Game.Furniture;
using Sadie.Enums.Miscellaneous;
using Sadie.Networking.Writers.Rooms.Furniture;

namespace Sadie.Game.Rooms.Furniture;

public class RoomFurnitureItemHelperService : IRoomFurnitureItemHelperService
{
    public async Task CycleInteractionStateForItemAsync(
        IRoomLogic room, 
        PlayerFurnitureItemPlacementData roomFurnitureItem,
        IDbContextFactory<SadieDbContext> dbContextFactory)
    {
        if (string.IsNullOrEmpty(roomFurnitureItem.PlayerFurnitureItem.MetaData))
        {
            roomFurnitureItem.PlayerFurnitureItem.MetaData = 0.ToString();
        }

        if (roomFurnitureItem.FurnitureItem.InteractionModes < 1 ||
            !int.TryParse(roomFurnitureItem.PlayerFurnitureItem.MetaData, out var state))
        {
            return;
        }

        // Cycle 0..(modes-1) so state 0 (off/default) is reachable. The original
        // `(state >= modes ? 0 : state) + 1` produced 1..modes and never returned
        // to 0, so toggle furni (lamps, gates) could turn on but never off.
        var nextState = (state + 1) % roomFurnitureItem.FurnitureItem.InteractionModes;

        await UpdateMetaDataForItemAsync(room, roomFurnitureItem, nextState.ToString());
        
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        
        dbContext.Entry(roomFurnitureItem.PlayerFurnitureItem!).Property(x => x.MetaData).IsModified = true;
        await dbContext.SaveChangesAsync();
    }

    public async Task UpdateMetaDataForItemAsync(
        IRoomLogic room, 
        PlayerFurnitureItemPlacementData roomFurnitureItem, 
        string metaData)
    {
        roomFurnitureItem.PlayerFurnitureItem.MetaData = metaData;
        await BroadcastItemUpdateToRoomAsync(room, roomFurnitureItem);
    }

    public async Task BroadcastItemUpdateToRoomAsync(
        IRoomLogic room, 
        PlayerFurnitureItemPlacementData roomFurnitureItem)
    {
        AbstractPacketWriter itemWriter = roomFurnitureItem.FurnitureItem.Type == FurnitureItemType.Floor ? 
            new RoomFloorItemUpdatedWriter
            {
                Id = roomFurnitureItem.PlayerFurnitureItemId,
                AssetId = roomFurnitureItem.FurnitureItem.AssetId,
                PositionX = roomFurnitureItem.PositionX,
                PositionY = roomFurnitureItem.PositionY,
                Direction = (int)roomFurnitureItem.Direction,
                PositionZ = roomFurnitureItem.PositionZ,
                StackHeight = 0.ToString(),
                Extra = 0,
                ObjectDataKey = (int) GetObjectDataKeyForItem(roomFurnitureItem),
                ObjectData = GetObjectDataForItem(roomFurnitureItem),
                MetaData = roomFurnitureItem.PlayerFurnitureItem.MetaData,
                Expires = -1,
                InteractionModes = 1,
                OwnerId = roomFurnitureItem.PlayerFurnitureItem.PlayerId
            }
            : new Sadie.Networking.Events.Writers.FixedRoomWallFurnitureItemUpdatedWriter
        {
            Item = roomFurnitureItem
        };
        
        await room.UserRepository.BroadcastDataAsync(itemWriter);
    }

    public ObjectDataKey GetObjectDataKeyForItem(PlayerFurnitureItemPlacementData furnitureItem)
    {
        return furnitureItem.FurnitureItem.InteractionType switch
        {
            FurnitureItemInteractionType.RoomAdsBg => ObjectDataKey.MapKey,
            _ => ObjectDataKey.LegacyKey
        };
    }

    public Dictionary<string, string> GetObjectDataForItem(PlayerFurnitureItemPlacementData furnitureItem)
    {
        if (furnitureItem.FurnitureItem!.InteractionType == FurnitureItemInteractionType.RoomAdsBg)
        {
            var data = new Dictionary<string, string>();
            
            foreach (var piece in furnitureItem.PlayerFurnitureItem.MetaData.Split(";"))
            {
                var parts = piece.Split("=");
                var key = parts[0];
                var value = parts.Length < 2 ? "" : parts[1];

                data[key] = value;
            }

            return data;
        }

        return new Dictionary<string, string>();
    }
}