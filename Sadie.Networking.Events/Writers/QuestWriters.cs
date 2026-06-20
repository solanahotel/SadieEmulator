using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Economy;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Daily Quests list (9018): the player's quests with today's progress + claim state.
[PacketId(9018)]
public class QuestsListWriter : AbstractPacketWriter
{
    public required IReadOnlyList<QuestService.QuestView> Quests { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Quests.Count);
        foreach (var q in Quests)
        {
            writer.WriteString(q.Code);
            writer.WriteString(q.Name);
            writer.WriteString(q.Description);
            writer.WriteInteger(q.Goal);
            writer.WriteInteger(q.Progress);
            writer.WriteInteger(q.RewardCredits);
            writer.WriteBool(q.Claimed);
        }
    }
}
