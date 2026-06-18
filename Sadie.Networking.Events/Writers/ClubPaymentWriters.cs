using Sadie.API;
using Sadie.API.Networking;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Server -> client responses for the custom Solana Club payment flow.
// All numeric values are sent as STRINGS: lamports can exceed int32 (the codec's
// WriteInteger/WriteLong both emit only 4 bytes), and the client parses them as
// numbers/BigInt for the Phantom transaction.

[PacketId(EventHandlerId.ClubPaymentIntentResult)]
public class ClubPaymentIntentResultWriter : AbstractPacketWriter
{
    public bool Ok { get; init; }
    public int PackageId { get; init; }
    public string Treasury { get; init; } = "";
    public string Lamports { get; init; } = "0";
    public string Sol { get; init; } = "0";
    public string Reference { get; init; } = "";
    public string Network { get; init; } = "";
    public string PriceUsd { get; init; } = "";
    public string Error { get; init; } = "";

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Ok);
        writer.WriteInteger(PackageId);
        writer.WriteString(Treasury);
        writer.WriteString(Lamports);
        writer.WriteString(Sol);
        writer.WriteString(Reference);
        writer.WriteString(Network);
        writer.WriteString(PriceUsd);
        writer.WriteString(Error);
    }
}

[PacketId(EventHandlerId.ClubPaymentResult)]
public class ClubPaymentResultWriter : AbstractPacketWriter
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public int DaysLeft { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Ok);
        writer.WriteString(Message);
        writer.WriteInteger(DaysLeft);
    }
}
