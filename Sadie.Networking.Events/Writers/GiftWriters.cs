using Sadie.API;
using Sadie.API.Networking;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Sent when the gift recipient name typed in the catalog doesn't match a player.
[PacketId(1517)]
public class GiftReceiverNotFoundWriter : AbstractPacketWriter
{
    public override void OnSerialize(INetworkPacketWriter writer)
    {
        // No body — the client just shows "receiver not found".
    }
}

// PresentOpenedMessageEvent (header 56) — tells the client what came out of an opened
// present so it can show the "you received X" reveal. Matches PresentOpenedMessageParser:
// itemType(string), classId(int), productCode(string), placedItemId(int),
// placedItemType(string), placedInRoom(bool), petFigureString(string).
[PacketId(56)]
public class PresentOpenedWriter : AbstractPacketWriter
{
    public string ItemType { get; init; } = "S";
    public int ClassId { get; init; }
    public string ProductCode { get; init; } = "";
    public int PlacedItemId { get; init; }
    public string PlacedItemType { get; init; } = "S";
    public bool PlacedInRoom { get; init; }
    public string PetFigure { get; init; } = "";

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteString(ItemType);
        writer.WriteInteger(ClassId);
        writer.WriteString(ProductCode);
        writer.WriteInteger(PlacedItemId);
        writer.WriteString(PlacedItemType);
        writer.WriteBool(PlacedInRoom);
        writer.WriteString(PetFigure);
    }
}
