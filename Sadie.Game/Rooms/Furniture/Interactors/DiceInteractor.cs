using System.Drawing;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Furniture;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Shared;

namespace Sadie.Game.Rooms.Furniture.Interactors;

public class DiceInteractor(
    IRoomFurnitureItemHelperService roomFurnitureItemHelperService,
    IRoomTileMapHelperService tileMapHelperService)
    : AbstractRoomFurnitureItemInteractor
{
    public override List<string> InteractionTypes => ["dice"];

    public override async Task OnTriggerAsync(IRoomLogic room,
        PlayerFurnitureItemPlacementData item,
        IRoomUser roomUser)
    {
        // Must be standing next to the dice to roll it — applies to every trigger
        // path (double-click AND the "Use" button, both routed through here).
        var itemPosition = new Point(item.PositionX, item.PositionY);

        if (tileMapHelperService.GetSquaresBetweenPoints(itemPosition, roomUser.Point) > 1)
        {
            return;
        }

        await roomFurnitureItemHelperService.UpdateMetaDataForItemAsync(room,
            item, "-1");

        await Task.Delay(1500);

        // Next(min, max) is max-EXCLUSIVE, so use 7 to roll a full 1-6 (was 1-5, never 6).
        await roomFurnitureItemHelperService.UpdateMetaDataForItemAsync(room,
            item,
            GlobalState.Random.Next(1,
                    7)
                .ToString());
    }
}
