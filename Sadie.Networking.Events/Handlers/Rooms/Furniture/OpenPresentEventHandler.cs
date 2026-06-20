using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Db.Models.Furniture;
using Sadie.Db.Models.Players;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Players;
using Sadie.Networking.Writers.Players.Inventory;
using Sadie.Networking.Writers.Rooms.Furniture;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Rooms.Furniture;

// Open a placed present: hand the wrapped item (recorded in player_gifts) to the opener,
// remove the present from the room/inventory, and tell the client what came out (header 56).
[PacketId(EventHandlerId.OpenPresent)]
public class OpenPresentEventHandler(
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IRoomTileMapHelperService tileMapHelperService) : INetworkPacketEventHandler
{
    public int ItemId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null || client.RoomUser == null)
        {
            return;
        }

        var room = client.RoomUser.Room;

        var present = room.FurnitureItems.FirstOrDefault(x => x.PlayerFurnitureItemId == ItemId);

        // Only the owner can open their own present.
        if (present?.PlayerFurnitureItem == null || present.PlayerFurnitureItem.PlayerId != client.Player.Id)
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var baseIds = await dbContext.Database
            .SqlQueryRaw<long>("SELECT base_furniture_item_id AS Value FROM player_gifts WHERE present_item_id = {0}", (long) ItemId)
            .ToListAsync();

        if (baseIds.Count == 0)
        {
            return;
        }

        var wrappedId = baseIds[0];

        // Rare Box: roll one of the configured pool items at open time (each equally likely), so the
        // owner's Housekeeping changes apply to every box. The granted item is a normal tradeable rare.
        if (present.FurnitureItem.AssetName == "rare_box")
        {
            var pool = await dbContext.Database
                .SqlQueryRaw<long>("SELECT furniture_item_id AS Value FROM rare_box_items")
                .ToListAsync();
            if (pool.Count == 0)
            {
                pool = await dbContext.Database
                    .SqlQueryRaw<long>("SELECT id AS Value FROM furniture_items WHERE marketable = 1 AND rarity = 'rare' ORDER BY RAND() LIMIT 1")
                    .ToListAsync();
            }
            if (pool.Count > 0) wrappedId = pool[Random.Shared.Next(pool.Count)];
        }

        var wrappedDef = await dbContext.Set<FurnitureItem>().FirstOrDefaultAsync(x => x.Id == wrappedId);
        var openerPlayer = await dbContext.Set<Player>().FirstOrDefaultAsync(x => x.Id == client.Player.Id);

        if (wrappedDef == null || openerPlayer == null)
        {
            return;
        }

        // Remove the present from the room.
        await room.UserRepository.BroadcastDataAsync(new RoomFloorFurnitureItemRemovedWriter
        {
            Id = present.PlayerFurnitureItemId.ToString(),
            Expired = false,
            OwnerId = 0,
            Delay = 0
        });

        var points = tileMapHelperService.GetPointsForPlacement(
            present.PositionX,
            present.PositionY,
            present.FurnitureItem.TileSpanX,
            present.FurnitureItem.TileSpanY,
            (int) present.Direction);

        room.FurnitureItems.Remove(present);
        tileMapHelperService.UpdateTileMapsForPoints(points, room.TileMap, room.FurnitureItems);

        // Give the wrapped item to the opener.
        var newItem = new PlayerFurnitureItem
        {
            Player = openerPlayer,
            FurnitureItem = wrappedDef,
            LimitedData = "1:1",
            MetaData = "",
            CreatedAt = DateTime.Now
        };

        dbContext.Entry(present).State = EntityState.Deleted;
        dbContext.Entry(present.PlayerFurnitureItem).State = EntityState.Deleted;
        dbContext.Entry(newItem).State = EntityState.Added;
        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM player_gifts WHERE present_item_id = {0}", (long) ItemId);

        // Keep the in-memory inventory in sync.
        var oldInventoryRecord = client.Player.FurnitureItems.FirstOrDefault(x => x.Id == ItemId);
        if (oldInventoryRecord != null)
        {
            client.Player.FurnitureItems.Remove(oldInventoryRecord);
        }

        client.Player.FurnitureItems.Add(newItem);

        await client.WriteToStreamAsync(new PresentOpenedWriter
        {
            ItemType = "S",
            ClassId = wrappedDef.AssetId,
            ProductCode = "",
            PlacedItemId = ItemId,
            PlacedItemType = "S",
            PlacedInRoom = false,
            PetFigure = ""
        });

        await client.WriteToStreamAsync(new PlayerInventoryUnseenItemsWriter
        {
            Count = 1,
            Category = 1,
            FurnitureItems = new List<PlayerFurnitureItem> { newItem }
        });

        await client.WriteToStreamAsync(new PlayerInventoryRefreshWriter());
    }
}
