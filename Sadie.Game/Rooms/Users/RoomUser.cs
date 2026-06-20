using System.Drawing;
using Sadie.API;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Chat.Commands;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Game.Rooms.Pathfinding;
using Sadie.API.Game.Rooms.Services;
using Sadie.API.Game.Rooms.Users;
using Sadie.Db.Models.Constants;
using Sadie.Enums.Game.Furniture;
using Sadie.Enums.Game.Rooms;
using Sadie.Enums.Game.Rooms.Users;
using Sadie.Enums.Miscellaneous;
using Sadie.Game.Rooms.Unit;
using Sadie.Networking.Writers.Rooms.Users;
using Sadie.Networking.Writers.Rooms.Users.HandItems;

namespace Sadie.Game.Rooms.Users;

public class RoomUser(
    RoomLogic room,
    INetworkObject networkObject,
    Point point,
    double pointZ,
    HDirection directionHead,
    HDirection direction,
    IPlayerLogic player,
    ServerRoomConstants roomConstants,
    RoomControllerLevel controllerLevel,
    IRoomTileMapHelperService tileMapHelperService,
    IRoomHelperService roomHelperService,
    IRoomWiredService wiredService,
    IRoomPathFinderHelperService pathFinderHelperService)
    : RoomUnitData(
            room,
            point,
            pointZ,
            directionHead,
            direction,
            tileMapHelperService,
            pathFinderHelperService),
        IRoomUser
{
    public IPlayerLogic Player { get; } = player;
    public DateTime LastAction { get; set; } = DateTime.Now;
    public TimeSpan IdleTime { get; } = TimeSpan.FromSeconds(roomConstants.SecondsTillUserIdle);
    public bool IsIdle { get; set; }
    public bool MoonWalking { get; set; }
    public IRoomUserTrade? Trade { get; set; }
    public int TradeStatus { get; set; }
    public int ActiveEffectId { get; set; }
    // Tracks tile-sourced effects so a player's chosen effect (set via inventory / :enable) survives
    // walking: tile effects temporarily override the chosen one and it is restored on stepping off.
    private int _chosenEffectId;
    private bool _onTileEffect;
    public IRoomLogic Room { get; } = room;
    public RoomControllerLevel ControllerLevel { get; set; } = controllerLevel;
    public INetworkObject NetworkObject { get; } = networkObject;
    public DateTime SignSet { get; set; } = DateTime.MinValue;

    public void LookAtPoint(Point point)
    {
        var direction = pathFinderHelperService.GetDirectionForNextStep(Point, point);

        if (!StatusMap.ContainsKey(RoomUserStatus.Sit))
        {
            Direction = direction;
        }

        if (Math.Abs(direction - Direction) < 2)
        {
            DirectionHead = direction;
        }
    }

    public void ApplyFlatCtrlStatus()
    {
        AddStatus(RoomUserStatus.FlatCtrl, ((int)controllerLevel).ToString());
    }

    public async Task RunPeriodicCheckAsync()
    {
        if (HandItemId != 0 && (DateTime.Now - HandItemSet).TotalSeconds >= 30)
        {
            HandItemId = 0;
        
            await room.UserRepository.BroadcastDataAsync(new RoomUserHandItemWriter
            {
                UserId = Player.Id,
                ItemId = 0
            });
        }
        
        if (StatusMap.ContainsKey(RoomUserStatus.Sign) && 
            (DateTime.Now - SignSet).TotalSeconds >= 5)
        {
            RemoveStatuses(RoomUserStatus.Sign);
        }

        var position = Point;

        await ProcessGenericChecksAsync();
        await UpdateIdleStatusAsync();
        await UpdateEffectAsync();
        
        if (Point != position)
        {
            await CheckForStepTriggersAsync(position, FurnitureItemInteractionType.WiredTriggerUserWalksOffFurniture);
            await CheckForStepTriggersAsync(Point, FurnitureItemInteractionType.WiredTriggerUserWalksOnFurniture);
        }
    }
    
    private async Task CheckForStepTriggersAsync(Point point, string interactionType)
    {
        var itemIdsOnPoint = tileMapHelperService
            .GetItemsForPosition(point.X, point.Y, room.FurnitureItems)
            .Select(x => x.Id)
            .ToList();

        if (itemIdsOnPoint.Count < 1)
        {
            return;
        }
        
        var triggers = wiredService.GetTriggers(
            interactionType,
            room.FurnitureItems,
            "",
            itemIdsOnPoint);
        
        foreach (var trigger in triggers)
        {
            await wiredService.RunTriggerForRoomAsync(room, trigger, this);
        }
    }
    
    private async Task UpdateEffectAsync()
    {
        var effectPointToCheck = IsWalking && NextPoint != null ? 
            NextPoint.Value : 
            Point;
        
        var tileEffect = room.TileMap.EffectMap[effectPointToCheck.Y, effectPointToCheck.X];

        if (tileEffect != 0)
        {
            // Stepping onto a tile effect: remember the chosen effect, then show the tile effect.
            if (!_onTileEffect) _chosenEffectId = ActiveEffectId;
            _onTileEffect = true;

            if (ActiveEffectId != tileEffect)
            {
                await SetEffectAsync((RoomUserEffect) tileEffect);
            }
        }
        else if (_onTileEffect)
        {
            // Stepped off a tile effect: restore the player's chosen effect (0 if none).
            _onTileEffect = false;

            if (ActiveEffectId != _chosenEffectId)
            {
                await SetEffectAsync((RoomUserEffect) _chosenEffectId);
            }
        }
        // Otherwise (no tile effect, not coming off one) leave ActiveEffectId alone so a chosen
        // effect persists while walking.
    }

    private async Task UpdateIdleStatusAsync()
    {
        var shouldBeIdle = DateTime.Now - LastAction > IdleTime;

        if (shouldBeIdle && !IsIdle || !shouldBeIdle && IsIdle)
        {
            IsIdle = shouldBeIdle;

            var writer = new RoomUserIdleWriter
            {
                UserId = Player.Id,
                IsIdle = IsIdle
            };
            
            await room.UserRepository.BroadcastDataAsync(writer);
        }
    }

    public bool HasRights()
    {
        return ControllerLevel is 
            RoomControllerLevel.Owner or 
            RoomControllerLevel.Rights;
    }

    public async Task SendWhisperAsync(string message)
    {
        await NetworkObject.WriteToStreamAsync(new RoomUserWhisperWriter
        {
            SenderId = Player.Id,
            Message = message,
            EmotionId = (int) roomHelperService.GetEmotionFromMessage(message),
            ChatBubbleId = 0,
            MessageLength = message.Length,
            Urls = []
        });
    }

    public async Task SetEffectAsync(RoomUserEffect effect)
    {
        ActiveEffectId = (int) effect;
        
        var writer = new RoomUserEffectWriter
        {
            UserId = (int) Player.Id,
            EffectId = (int) effect,
            DelayMs = 0
        };

        await Room.UserRepository.BroadcastDataAsync(writer);
    }
    public async ValueTask DisposeAsync()
    {
        if (room.TileMap.UnitMap.TryGetValue(Point, out var value))
        {
            value.Remove(this);
        }
    }
}