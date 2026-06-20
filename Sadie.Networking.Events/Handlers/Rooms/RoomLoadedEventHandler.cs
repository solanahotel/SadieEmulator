using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sadie.API.Game.Players;
using Sadie.API.Game.Rooms;
using Sadie.API.Game.Rooms.Furniture;
using Sadie.API.Game.Rooms.Mapping;
using Sadie.API.Game.Rooms.Services;
using Sadie.API.Game.Rooms.Users;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Enums.Game.Players;
using Sadie.Enums.Game.Rooms;
using Sadie.Enums.Miscellaneous;
using Sadie.Networking.Events.Achievements;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Writers.Generic;
using Sadie.Networking.Writers.Rooms;
using Sadie.Networking.Writers.Rooms.Doorbell;
using Sadie.Networking.Writers.Rooms.Users;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Rooms;

[PacketId(EventHandlerId.RoomLoaded)]
public class RoomLoadedEventHandler(
    ILogger<RoomLoadedEventHandler> logger,
    IRoomRepository roomRepository,
    IRoomUserFactory roomUserFactory,
    IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory,
    IMapper mapper,
    IRoomTileMapHelperService tileMapHelperService,
    IPlayerHelperService playerHelperService,
    IRoomFurnitureItemHelperService roomFurnitureItemHelperService,
    IRoomWiredService wiredService)
    : INetworkPacketEventHandler
{
    public int RoomId { get; init; }
    public required string Password { get; init; }
    
    public async Task HandleAsync(INetworkClient client)
    {
        var player = client.Player;

        if (player == null)
        {
            return;
        }

        var room = await RoomHelpers.TryLoadRoomByIdAsync(
            RoomId,
            roomRepository,
            dbContextFactory,
            mapper);
        
        var lastRoomId = player.State.CurrentRoomId;
        
        if (lastRoomId != 0)
        {
            var lastRoom = await RoomHelpers.TryLoadRoomByIdAsync(lastRoomId,
                roomRepository,
                dbContextFactory, 
                mapper);

            if (lastRoom != null && lastRoom.UserRepository.TryGetById(player.Id, out var existingUser) && existingUser != null)
            {
                await lastRoom.UserRepository.TryRemoveAsync(existingUser.Player.Id);
            }
        }

        if (room == null)
        {
            logger.LogError($"Failed to load room {RoomId} for player '{player.Username}'");
            await client.WriteToStreamAsync(new RoomUserHotelViewWriter());
            
            return;
        }

        var isOwner = room.OwnerId == player.Id;
        // Admins (any-room-rights) enter any room, bypassing capacity, doorbell and password.
        var isStaff = player.HasPermission(PlayerPermissionName.AnyRoomRights);

        if (room.UserRepository.Count >= room.MaxUsersAllowed && !isOwner && !isStaff)
        {
            await client.WriteToStreamAsync(new RoomEnterErrorWriter
            {
                ErrorCode = (int) RoomEnterError.NoCapacity
            });

            return;
        }

        if (room.Settings.AccessType is RoomAccessType.Doorbell or RoomAccessType.Password &&
            !isOwner && !isStaff &&
            !await ValidateRoomAccessForClientAsync(client, room, Password))
        {
            return;
        }
        
        await RoomEntryEventHelpers.GenericEnterRoomAsync(
            client,
            room,
            roomUserFactory,
            dbContextFactory,
            playerRepository,
            tileMapHelperService,
            playerHelperService,
            roomFurnitureItemHelperService,
            wiredService);

        // Achievement: entering rooms (counts each successful entry).
        try { await AchievementService.AddProgressAsync(dbContextFactory, player, "RoomEntry", 1); } catch { /* non-fatal */ }

        // Faucets (only when visiting someone else's room): reward the owner per unique visitor/day,
        // and reward the visitor per unique room/day. Non-fatal so it never blocks room entry.
        try
        {
            if (!isOwner)
            {
                if (await FaucetService.TryClaimUniqueAsync(dbContextFactory, room.OwnerId, "room_visitor", player.Id))
                {
                    await FaucetService.GrantAsync(playerRepository, dbContextFactory, room.OwnerId, "room_visitors",
                        FaucetService.VisitorAmount, FaucetService.VisitorCap, await FaucetService.IsMemberAsync(dbContextFactory, room.OwnerId));
                    await QuestService.AddProgressAsync(dbContextFactory, room.OwnerId, "room_visitors", 1);
                }

                if (await FaucetService.TryClaimUniqueAsync(dbContextFactory, player.Id, "room_visit", RoomId))
                {
                    await FaucetService.GrantAsync(playerRepository, dbContextFactory, player.Id, "visit_rooms",
                        FaucetService.VisitAmount, FaucetService.VisitCap, await FaucetService.IsMemberAsync(dbContextFactory, player.Id));
                    await QuestService.AddProgressAsync(dbContextFactory, player.Id, "visit_rooms", 1);
                }
            }
        }
        catch { /* non-fatal */ }

        // Re-apply the player's equipped avatar effect so it persists across rooms / relog. Non-fatal.
        try
        {
            var activeEffect = await Sadie.Networking.Events.Effects.EffectService.GetActiveAsync(dbContextFactory, player.Id);
            if (activeEffect != 0 &&
                NetworkPacketEventHelpers.TryResolveRoomObjectsForClient(roomRepository, client, out _, out var selfUser))
            {
                selfUser.ActiveEffectId = activeEffect;
                await selfUser.Room.UserRepository.BroadcastDataAsync(new RoomUserEffectWriter
                {
                    UserId = (int) selfUser.Player.Id,
                    EffectId = activeEffect,
                    DelayMs = 0
                });
            }
        }
        catch { /* non-fatal */ }
    }

    private static async Task<bool> ValidateRoomAccessForClientAsync(INetworkClient client, IRoomLogic room, string password)
    {
        var player = client.Player!;
        
        switch (room.Settings.AccessType)
        {
            case RoomAccessType.Password:
                if (room.Settings.Password == password)
                {
                    return true;
                }
                
                await client.WriteToStreamAsync(new GenericErrorWriter
                {
                    ErrorCode = (int) GenericErrorCode.NavigatorInvalidPassword
                });
                
                await client.WriteToStreamAsync(new RoomUserHotelViewWriter());
                return false;
            
            case RoomAccessType.Doorbell:
            {
                var usersWithRights = room.UserRepository.GetAllWithRights();

                if (usersWithRights.Count < 1)
                {
                    await client.WriteToStreamAsync(new RoomDoorbellNoAnswerWriter
                    {
                        Username = player.Username
                    });
                    
                    return false;
                }
                
                foreach (var user in usersWithRights)
                {
                    await user.NetworkObject.WriteToStreamAsync(new RoomDoorbellWriter
                    {
                        Username = player.Username
                    });
                    
                }

                await client.WriteToStreamAsync(new RoomDoorbellWriter
                {
                    Username = ""
                });
                
                return false;
            }
            case RoomAccessType.Open:
            case RoomAccessType.Invisible:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return true;
    }
}