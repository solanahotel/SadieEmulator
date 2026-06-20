using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Networking.Events.Commands;
using Sadie.Networking.Writers.Players;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Players;

[PacketId(EventHandlerId.PlayerSanctionStatus)]
public class PlayerSanctionStatusEventHandler : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        var playerId = client.Player?.Id ?? 0;

        // Reflect the live mod-tool sanctions: a mute or trade lock shows up in the player's own
        // sanction-status panel. The writer renders TradeLockedUntil == MinValue as an empty string.
        await client.WriteToStreamAsync(new PlayerSanctionStatusWriter
        {
            HasPreviousSanction = false,
            OnProbation = false,
            SanctionName = "ALERT",
            SanctionLengthHours = 0,
            Unknown1 = 30,
            Reason = "cfh.reason.EMPTY",
            ProbationStart = DateTime.Now,
            Unknown2 = 0,
            NextSanctionType = "ALERT",
            HoursForNextSanction = 0,
            Unknown3 = 30,
            Muted = GlobalMuteStore.IsMuted(playerId),
            TradeLockedUntil = TradeLockStore.TryGetExpiry(playerId) ?? DateTime.MinValue
        });
    }
}