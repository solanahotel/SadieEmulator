using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Economy;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Support ticket packets. 9043 = ticket list, 9044 = one ticket's conversation, 9045 = result,
// 9046 = "show the first-time guide".

[PacketId(9043)]
public class SupportTicketsWriter : AbstractPacketWriter
{
    public required IReadOnlyList<SupportService.TicketView> Tickets { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Tickets.Count);
        foreach (var t in Tickets)
        {
            writer.WriteInteger(t.Id);
            writer.WriteString(t.Subject);
            writer.WriteString(t.Category);
            writer.WriteString(t.Status);
            writer.WriteString(t.OwnerName);
            writer.WriteInteger(t.Messages);
            writer.WriteString(t.UpdatedAt);
        }
    }
}

[PacketId(9044)]
public class SupportTicketWriter : AbstractPacketWriter
{
    public required SupportService.TicketDetail Ticket { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Ticket.Id);
        writer.WriteString(Ticket.Subject);
        writer.WriteString(Ticket.Category);
        writer.WriteString(Ticket.Status);
        writer.WriteString(Ticket.OwnerName);
        writer.WriteInteger(Ticket.Messages.Count);
        foreach (var m in Ticket.Messages)
        {
            writer.WriteString(m.SenderName);
            writer.WriteBool(m.IsStaff);
            writer.WriteString(m.Body);
            writer.WriteString(m.CreatedAt);
        }
    }
}

// Silent live update of one ticket's conversation (push on staff reply, or poll response). Same payload
// as 9044 but the client applies it without changing the user's current view.
[PacketId(9047)]
public class SupportTicketUpdateWriter : AbstractPacketWriter
{
    public required SupportService.TicketDetail Ticket { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Ticket.Id);
        writer.WriteString(Ticket.Subject);
        writer.WriteString(Ticket.Category);
        writer.WriteString(Ticket.Status);
        writer.WriteString(Ticket.OwnerName);
        writer.WriteInteger(Ticket.Messages.Count);
        foreach (var m in Ticket.Messages)
        {
            writer.WriteString(m.SenderName);
            writer.WriteBool(m.IsStaff);
            writer.WriteString(m.Body);
            writer.WriteString(m.CreatedAt);
        }
    }
}

[PacketId(9045)]
public class SupportResultWriter : AbstractPacketWriter
{
    public required bool Ok { get; init; }
    public required string Message { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Ok);
        writer.WriteString(Message);
    }
}

[PacketId(9046)]
public class SupportGuideWriter : AbstractPacketWriter
{
    public required bool Show { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer) => writer.WriteBool(Show);
}
