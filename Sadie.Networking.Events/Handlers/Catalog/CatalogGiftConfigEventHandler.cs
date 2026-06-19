using Sadie.API.Networking.Client;
using Sadie.API.Networking.Events.Handlers;
using Sadie.Networking.Events.Writers;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Handlers.Catalog;

[PacketId(EventHandlerId.CatalogGiftConfig)]
public class CatalogGiftConfigEventHandler : INetworkPacketEventHandler
{
    public async Task HandleAsync(INetworkClient client)
    {
        // The present_gen* boxes (sprite ids 187-193). Listing them as both box types and
        // the "default" (gift furni) types makes every box a free, ready-to-send wrapping with
        // no extra paper/colour step — the client shows them and lets the user gift.
        var boxes = new List<int> { 187, 188, 189, 190, 191, 192, 193 };

        await client.WriteToStreamAsync(new FixedGiftWrappingConfigWriter
        {
            Enabled = true,
            Price = 0,
            GiftWrappers = [],
            BoxTypes = boxes,
            RibbonTypes = [0],
            GiftFurniture = boxes,
        });
    }
}