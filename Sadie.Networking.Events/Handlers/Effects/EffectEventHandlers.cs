using Microsoft.EntityFrameworkCore;
using Sadie.API.Game.Rooms;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Effects;
using Sadie.Networking.Events.Writers;
using Sadie.Networking.Writers.Rooms.Users;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Effects;

// Effects inventory handlers (custom protocol). 9051 = fetch list, 9052 = activate, 9053 = deactivate.
// Rendering reuses the standard RoomUserEffectWriter (same packet the :enable command uses).

[PacketId(9051)]
public class GetEffectsInventoryEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var effects = await EffectService.GetInventoryAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new EffectsInventoryWriter { Effects = effects });
    }
}

[PacketId(9052)]
public class ActivateEffectEventHandler(
    IRoomRepository roomRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public int EffectId { get; set; }

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        if (!await EffectService.OwnsAsync(dbContextFactory, client.Player.Id, EffectId)) return;

        await EffectService.ActivateAsync(dbContextFactory, client.Player.Id, EffectId);
        await ApplyEffectAsync(roomRepository, client, EffectId);
        await client.WriteToStreamAsync(new EffectsInventoryWriter
        {
            Effects = await EffectService.GetInventoryAsync(dbContextFactory, client.Player.Id)
        });
    }

    // Set the persistent (walk-surviving) effect on the room user and broadcast it to the room.
    internal static async Task ApplyEffectAsync(IRoomRepository roomRepository, INetworkClient client, int effectId)
    {
        if (!NetworkPacketEventHelpers.TryResolveRoomObjectsForClient(roomRepository, client, out _, out var roomUser))
            return;

        roomUser.ActiveEffectId = effectId;

        await roomUser.Room.UserRepository.BroadcastDataAsync(new RoomUserEffectWriter
        {
            UserId = (int) roomUser.Player.Id,
            EffectId = effectId,
            DelayMs = 0
        });
    }
}

[PacketId(9053)]
public class DeactivateEffectEventHandler(
    IRoomRepository roomRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        await EffectService.DeactivateAsync(dbContextFactory, client.Player.Id);
        await ActivateEffectEventHandler.ApplyEffectAsync(roomRepository, client, 0);
        await client.WriteToStreamAsync(new EffectsInventoryWriter
        {
            Effects = await EffectService.GetInventoryAsync(dbContextFactory, client.Player.Id)
        });
    }
}
