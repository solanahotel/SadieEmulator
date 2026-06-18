using System.Drawing;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Furniture;
using Sadie.API.Game.Rooms.Furniture.Processors;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Game.Rooms.Users;
using Sadie.API.Networking;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Enums.Game.Furniture;
using Sadie.Enums.Game.Rooms.Mapping;
using Sadie.Enums.Game.Rooms.Users;
using Sadie.Networking.Writers.Rooms.Furniture;

namespace Sadie.Game.Rooms.Furniture.Processors;

public class RollerProcessor(IRoomTileMapHelperService tileMapHelperService,
    IRoomFurnitureItemHelperService roomFurnitureItemHelperService) : IRoomFurnitureItemProcessor
{
    public async Task<IEnumerable<AbstractPacketWriter>> GetUpdatesForRoomAsync(IRoomLogic room)
    {
        var writers = new List<AbstractPacketWriter>();
        
        var roomRollers = room
            .FurnitureItems
            .Where(x => x.FurnitureItem!.InteractionType == FurnitureItemInteractionType.Roller);
        
        var rollerUpdates = await GetRollerUpdatesAsync(room, roomRollers);
        
        writers.AddRange(rollerUpdates);
        return writers;
    }

    private async Task<IEnumerable<RoomObjectsRollingWriter>> GetRollerUpdatesAsync(
        IRoomLogic room, 
        IEnumerable<PlayerFurnitureItemPlacementData> rollers)
    {
        var writers = new List<RoomObjectsRollingWriter>();
        var userIdsProcessed = new HashSet<long>();
        var itemIdsProcessed = new HashSet<int>();

        foreach (var roller in rollers)
        {
            var x = roller.PositionX;
            var y = roller.PositionY;
            
            var nextStep = tileMapHelperService.GetPointInFront(x, y, roller.Direction);
            
            var rollerPosition = new Point(x, y);
            
            var nextRoller = tileMapHelperService
                .GetItemsForPosition(nextStep.X, nextStep.Y, room.FurnitureItems)
                .FirstOrDefault(fi => fi.FurnitureItem!.InteractionType == FurnitureItemInteractionType.Roller);
            
            var nextHeight = nextRoller?.FurnitureItem?.StackHeight ?? 0;
            
            var users = room.UserRepository
                .GetAll()
                .Where(u => !userIdsProcessed.Contains(u.Player.Id));

            // Can't roll past the edge of the room.
            if (!room.TileMap.TileExists(nextStep))
            {
                continue;
            }

            var nextStepHasUser = room.TileMap.UsersAtPoint(nextStep);

            // Users only roll onto an open, unoccupied tile.
            if (room.TileMap.Map[nextStep.Y, nextStep.X] == (int)RoomTileState.Open && !nextStepHasUser)
            {
                var rollingUsers = tileMapHelperService.GetUsersAtPoints([rollerPosition], users);

                foreach (var rollingUser in rollingUsers)
                {
                    await MoveUserOnRollerAsync(
                        x,
                        y,
                        nextStep,
                        userIdsProcessed,
                        rollingUser,
                        writers,
                        room,
                        roller,
                        nextRoller,
                        nextHeight);
                }
            }

            // A user standing in front blocks items too; otherwise items keep rolling
            // even onto an occupied tile and STACK on whatever is there (so placing an
            // item in front of a roller no longer stops the transport).
            if (nextStepHasUser)
            {
                continue;
            }

            var unprocessedNonRollers = room.FurnitureItems.Where(i =>
                !itemIdsProcessed.Contains(i.Id) && i.FurnitureItem!.InteractionType !=
                FurnitureItemInteractionType.Roller);

            var nonRollerItemsOnRoller = tileMapHelperService.GetItemsForPosition(
                roller.PositionX,
                roller.PositionY,
                unprocessedNonRollers);

            if (nonRollerItemsOnRoller.Count == 0)
            {
                continue;
            }

            foreach (var item in nonRollerItemsOnRoller)
            {
                var oldPoints = tileMapHelperService.GetPointsForPlacement(
                    item.PositionX,
                    item.PositionY,
                    item.FurnitureItem.TileSpanX,
                    item.FurnitureItem.TileSpanY,
                    (int) item.Direction);

                var newPoints = tileMapHelperService.GetPointsForPlacement(
                    nextStep.X, nextStep.Y,
                    item.FurnitureItem.TileSpanX,
                    item.FurnitureItem.TileSpanY,
                    (int) item.Direction);

                // Land on top of whatever already occupies the destination tile.
                var landingHeight = tileMapHelperService.GetItemPlacementHeight(
                    room.TileMap,
                    newPoints,
                    room.FurnitureItems.Except([item]).ToList());

                MoveItemOnRoller(
                    nextStep,
                    itemIdsProcessed,
                    writers,
                    item,
                    roller,
                    landingHeight);

                tileMapHelperService.UpdateTileMapsForPoints(oldPoints,
                    room.TileMap,
                    room.FurnitureItems);

                tileMapHelperService.UpdateTileMapsForPoints(newPoints,
                    room.TileMap,
                    room
                        .FurnitureItems
                        .Except([item])
                        .ToList());

                await roomFurnitureItemHelperService.BroadcastItemUpdateToRoomAsync(room, item);
            }
        }

        return writers;
    }
    
    private void MoveItemOnRoller(
        Point nextStep,
        ISet<int> itemIdsProcessed,
        ICollection<RoomObjectsRollingWriter> writers,
        PlayerFurnitureItemPlacementData item,
        PlayerFurnitureItemPlacementData roller,
        double nextHeight)
    {
        var rollingData = new RoomRollingObjectData
        {
            Id = item.PlayerFurnitureItemId,
            Height = item.PositionZ.ToString(),
            NextHeight = nextHeight.ToString()
        };
        
        writers.Add(new RoomObjectsRollingWriter
        {
            X = item.PositionX,
            Y = item.PositionY,
            NextX = nextStep.X,
            NextY = nextStep.Y,
            Objects = [rollingData],
            RollerId = roller.PlayerFurnitureItemId,
            MovementType = 2,
            RoomUserId = 0,
            Height = roller.PositionZ.ToString(),
            NextHeight = nextHeight.ToString()
        });
                
        item.PositionX = nextStep.X;
        item.PositionY = nextStep.Y;
        item.PositionZ = nextHeight;
                
        itemIdsProcessed.Add(item.PlayerFurnitureItemId);
    }

    private static async Task MoveUserOnRollerAsync(
        int x,
        int y,
        Point nextStep,
        ISet<long> playerIdsProcessed,
        IRoomUser rollingUser,
        ICollection<RoomObjectsRollingWriter> writers,
        IRoomLogic room,
        PlayerFurnitureItemPlacementData roller,
        PlayerFurnitureItemPlacementData? nextRoller,
        double nextHeight)
    {
        playerIdsProcessed.Add(rollingUser.Player.Id);

        if (rollingUser.StatusMap.ContainsKey(RoomUserStatus.Move))
        {
            return;
        }

        writers.Add(new RoomObjectsRollingWriter
        {
            X = x,
            Y = y,
            NextX = nextStep.X,
            NextY = nextStep.Y,
            Objects = [],
            RollerId = roller.PlayerFurnitureItemId,
            MovementType = 2,
            RoomUserId = rollingUser.Player.Id,
            Height = rollingUser.PointZ.ToString(),
            NextHeight = nextHeight.ToString()
        });

        room.TileMap.UnitMap[rollingUser.Point].Remove(rollingUser);
        room.TileMap.AddUnitToMap(nextStep, rollingUser);

        await rollingUser.SetPositionAsync(nextStep);
        
        rollingUser.PointZ = nextRoller?.FurnitureItem?.StackHeight ?? 0;
    }
}