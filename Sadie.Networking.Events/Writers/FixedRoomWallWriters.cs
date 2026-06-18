using Sadie.API;
using Sadie.API.Networking;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// The packaged wall-item writers identify items by the placement-record id
// (PlayerFurnitureItemPlacementData.Id), but the use/eject handlers look items up
// by PlayerFurnitureItem.Id (the inventory id) — same as floor items. The mismatch
// meant the client sent the wrong id, so wall items couldn't be used or picked up
// (only coincidental id matches worked). These replacements emit PlayerFurnitureItem.Id.

[PacketId(2187)] // RoomWallFurnitureItemPlacedWriter
public class FixedRoomWallFurnitureItemPlacedWriter : AbstractPacketWriter
{
    public PlayerFurnitureItemPlacementData RoomFurnitureItem { get; init; } = null!;

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteString(RoomFurnitureItem.PlayerFurnitureItem.Id.ToString());
        writer.WriteInteger(RoomFurnitureItem.FurnitureItem.AssetId);
        writer.WriteString(RoomFurnitureItem.WallPosition);
        writer.WriteString(RoomFurnitureItem.PlayerFurnitureItem.MetaData);
        writer.WriteInteger(-1);
        writer.WriteInteger(RoomFurnitureItem.FurnitureItem.InteractionModes > 1 ? 1 : 0);
        writer.WriteLong(RoomFurnitureItem.PlayerFurnitureItem.Player.Id);
        writer.WriteString(RoomFurnitureItem.PlayerFurnitureItem.Player.Username);
    }
}

[PacketId(2009)] // RoomWallFurnitureItemUpdatedWriter
public class FixedRoomWallFurnitureItemUpdatedWriter : AbstractPacketWriter
{
    public PlayerFurnitureItemPlacementData Item { get; init; } = null!;

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteString(Item.PlayerFurnitureItem.Id.ToString());
        writer.WriteInteger(Item.FurnitureItem.AssetId);
        writer.WriteString(Item.WallPosition);
        writer.WriteString(Item.PlayerFurnitureItem.MetaData);
        writer.WriteInteger(-1);
        writer.WriteInteger(Item.FurnitureItem.InteractionModes > 1 ? 1 : 0);
        writer.WriteLong(Item.PlayerFurnitureItem.Player.Id);
        writer.WriteString(Item.PlayerFurnitureItem.Player.Username);
    }
}

[PacketId(3208)] // RoomWallFurnitureItemRemovedWriter
public class FixedRoomWallFurnitureItemRemovedWriter : AbstractPacketWriter
{
    public PlayerFurnitureItemPlacementData Item { get; init; } = null!;

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteString(Item.PlayerFurnitureItem.Id.ToString());
        writer.WriteLong(Item.PlayerFurnitureItem.PlayerId);
    }
}
