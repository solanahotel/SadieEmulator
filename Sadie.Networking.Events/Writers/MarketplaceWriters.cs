using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Economy;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Outgoing marketplace packets (custom protocol, headers 9010-9015). The client's matching message
// events parse these field-for-field. Items are identified by asset_id so the client renders the furni.

// Browse listings for a section (MARKETPLACE_LISTINGS = 9010).
[PacketId(9010)]
public class MarketplaceListingsWriter : AbstractPacketWriter
{
    public required IReadOnlyList<MarketplaceService.ListingView> Listings { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Listings.Count);
        foreach (var listing in Listings) WriteListing(writer, listing);
    }

    internal static void WriteListing(INetworkPacketWriter writer, MarketplaceService.ListingView l)
    {
        writer.WriteInteger(l.Id);
        writer.WriteInteger(l.AssetId);
        writer.WriteString(l.Name);
        writer.WriteString(l.Rarity);
        writer.WriteInteger(l.Quantity);
        writer.WriteInteger(l.PriceCredits);
        writer.WriteString(l.PriceHotel);
        writer.WriteInteger(l.TotalCreditValue);
        writer.WriteInteger(l.StartPrice);
        writer.WriteInteger(l.HighestBid);
        writer.WriteInteger((int) l.ExpiresAt);
        writer.WriteString(l.SellerName);
    }
}

// The viewer's own active listings (MARKETPLACE_MY_LISTINGS = 9012).
[PacketId(9012)]
public class MarketplaceMyListingsWriter : AbstractPacketWriter
{
    public required IReadOnlyList<MarketplaceService.ListingView> Listings { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Listings.Count);
        foreach (var listing in Listings) MarketplaceListingsWriter.WriteListing(writer, listing);
    }
}

// The viewer's marketable inventory items, for the "list an item" picker (MARKETPLACE_SELLABLE = 9011).
[PacketId(9011)]
public class MarketplaceSellableWriter : AbstractPacketWriter
{
    public required IReadOnlyList<MarketplaceService.SellableView> Items { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Items.Count);
        foreach (var item in Items)
        {
            writer.WriteInteger(item.OwnedId);
            writer.WriteInteger(item.AssetId);
            writer.WriteString(item.Name);
            writer.WriteString(item.Rarity);
            writer.WriteInteger(item.CreditValue);
        }
    }
}

// Result of a list/buy/cancel action (MARKETPLACE_RESULT = 9013).
[PacketId(9013)]
public class MarketplaceResultWriter : AbstractPacketWriter
{
    public required bool Ok { get; init; }
    public required string Message { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Ok);
        writer.WriteString(Message);
    }
}
