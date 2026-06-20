using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Effects;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Effects inventory (custom protocol). 9050 = the player's owned effects list.
[PacketId(9050)]
public class EffectsInventoryWriter : AbstractPacketWriter
{
    public required List<OwnedEffect> Effects { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Effects.Count);
        foreach (var effect in Effects)
        {
            writer.WriteInteger(effect.EffectId);
            writer.WriteString(effect.Name);
            writer.WriteInteger(effect.Quantity);
            writer.WriteBool(effect.IsActive);
        }
    }
}
