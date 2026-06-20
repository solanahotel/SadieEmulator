using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Economy;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Persistent inbox packets (custom protocol). 9014 = the message list, 9015 = the live unread count.

[PacketId(9014)]
public class InboxMessagesWriter : AbstractPacketWriter
{
    public required IReadOnlyList<InboxService.InboxMessage> Messages { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Messages.Count);
        foreach (var m in Messages)
        {
            writer.WriteInteger(m.Id);
            writer.WriteString(m.Category);
            writer.WriteString(m.Title);
            writer.WriteString(m.Body);
            writer.WriteBool(m.IsRead);
            writer.WriteString(m.CreatedAt);
        }
    }
}

[PacketId(9015)]
public class InboxUnreadCountWriter : AbstractPacketWriter
{
    public required int Count { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer) => writer.WriteInteger(Count);
}
