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

// Gift wrapping config (header 2234). The shipped CatalogGiftWrappingConfigWriter relies on
// default serialization for its List<int> fields, which doesn't emit the lists — so the
// client's GiftWrappingConfigurationParser desyncs and giftConfiguration ends up unusable
// (the gift dialog then never opens). This writes the exact format the parser expects:
// isEnabled(bool), price(int), then four lists each as int count + int items
// (giftWrappers, boxTypes, ribbonTypes, giftFurnis).
[PacketId(2234)]
public class FixedGiftWrappingConfigWriter : AbstractPacketWriter
{
    public bool Enabled { get; init; }
    public int Price { get; init; }
    public List<int> GiftWrappers { get; init; } = [];
    public List<int> BoxTypes { get; init; } = [];
    public List<int> RibbonTypes { get; init; } = [];
    public List<int> GiftFurniture { get; init; } = [];

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Enabled);
        writer.WriteInteger(Price);
        WriteIntList(writer, GiftWrappers);
        WriteIntList(writer, BoxTypes);
        WriteIntList(writer, RibbonTypes);
        WriteIntList(writer, GiftFurniture);
    }

    private static void WriteIntList(INetworkPacketWriter writer, List<int> values)
    {
        writer.WriteInteger(values.Count);

        foreach (var value in values)
        {
            writer.WriteInteger(value);
        }
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
