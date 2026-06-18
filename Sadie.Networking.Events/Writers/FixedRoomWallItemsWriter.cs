using System.Collections.Generic;
using Sadie.API;
using Sadie.API.Networking;
using Sadie.Db.Models.Players.Furniture;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Drop-in replacement for Sadie.Networking.Writers.RoomWallItemsWriter (PacketId 1369),
// which has a bug: it serializes only WallItems and never writes the FurnitureOwners
// dictionary the client expects first (the floor-items writer does write it). Without
// the owner block the client mis-parses the packet, so wall items never render and
// therefore can't be picked up. This writes owners then items, mirroring the floor writer.
[PacketId(1369)]
public class FixedRoomWallItemsWriter : AbstractPacketWriter
{
    public Dictionary<long, string> FurnitureOwners { get; init; } = new();
    public ICollection<PlayerFurnitureItemPlacementData> WallItems { get; init; } = new List<PlayerFurnitureItemPlacementData>();

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(FurnitureOwners.Count);
        foreach (var owner in FurnitureOwners)
        {
            writer.WriteLong(owner.Key);
            writer.WriteString(owner.Value);
        }

        writer.WriteInteger(WallItems.Count);
        foreach (var item in WallItems)
        {
            writer.WriteString(item.PlayerFurnitureItem.Id.ToString());
            writer.WriteInteger(item.FurnitureItem.AssetId);
            writer.WriteString(item.WallPosition);
            writer.WriteString(item.PlayerFurnitureItem.MetaData);
            writer.WriteInteger(-1);
            writer.WriteInteger(item.FurnitureItem.InteractionModes > 1 ? 1 : 0);
            writer.WriteLong(item.PlayerFurnitureItem.PlayerId);
        }
    }
}
