using Microsoft.EntityFrameworkCore;
using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Db;
using Sadie.Networking.Events.Achievements;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Players;

// Client opened the achievements panel (ACHIEVEMENT_LIST = 219). Send the real, per-player computed
// list (the old handler sent an empty PlayerAchievementsWriter, so the panel was always blank).
[PacketId(EventHandlerId.PlayerAchievements)]
public class PlayerAchievementsEventHandler(IDbContextFactory<SadieDbContext> dbContextFactory) : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        if (client.Player == null) return;

        var achievements = await AchievementService.BuildListAsync(dbContextFactory, client.Player.Id);

        await client.WriteToStreamAsync(new AchievementsListWriter { Achievements = achievements });
    }
}
