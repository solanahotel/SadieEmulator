using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Economy;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Quests;

// Daily Quests. 9021 = fetch quests + progress, 9022 = claim a completed quest's reward.

[PacketId(9021)]
public class QuestsGetEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        var quests = await QuestService.GetQuestsAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new QuestsListWriter { Quests = quests });
    }
}

[PacketId(9022)]
public class QuestsClaimEventHandler(
    Sadie.API.Game.Players.IPlayerRepository playerRepository,
    IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public string Code { get; set; } = "";

    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;
        await QuestService.ClaimAsync(playerRepository, dbContextFactory, client.Player, Code);
        var quests = await QuestService.GetQuestsAsync(dbContextFactory, client.Player.Id);
        await client.WriteToStreamAsync(new QuestsListWriter { Quests = quests });
    }
}
