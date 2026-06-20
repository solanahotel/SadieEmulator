using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Wheel;

// Daily Wheel handlers. 9008 = fetch state, 9009 = spin.

[PacketId(9008)]
public class WheelGetStateEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var state = await WheelService.GetStateAsync(dbContextFactory, client.Player.Id, client.Player.HasPermission("admin"));
        await client.WriteToStreamAsync(new WheelStateWriter { CanSpin = state.CanSpin, LastReward = state.LastReward });
    }
}

[PacketId(9009)]
public class WheelSpinEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        bool isMember;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync())
        {
            isMember = (await dbContext.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM player_subscriptions WHERE player_id = {0} AND expires_at > {1}",
                client.Player.Id, DateTime.Now).ToListAsync()).FirstOrDefault() > 0;
        }

        var isAdmin = client.Player.HasPermission("admin");
        var result = await WheelService.SpinAsync(playerRepository, dbContextFactory, client.Player, isMember, isAdmin);
        await client.WriteToStreamAsync(new WheelResultWriter
        {
            Ok = result.Ok, RewardType = result.RewardType, Amount = result.Amount,
            Label = result.Label, Message = result.Ok ? "" : result.Error
        });

        var state = await WheelService.GetStateAsync(dbContextFactory, client.Player.Id, isAdmin);
        await client.WriteToStreamAsync(new WheelStateWriter { CanSpin = state.CanSpin, LastReward = state.LastReward });
    }
}
