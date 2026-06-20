using Sadie.API;
using Sadie.API.Networking;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Daily Wheel packets (custom protocol). 9016 = state (can spin / today's reward), 9017 = spin result.

[PacketId(9016)]
public class WheelStateWriter : AbstractPacketWriter
{
    public required bool CanSpin { get; init; }
    public required string LastReward { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(CanSpin);
        writer.WriteString(LastReward);
    }
}

[PacketId(9017)]
public class WheelResultWriter : AbstractPacketWriter
{
    public required bool Ok { get; init; }
    public required string RewardType { get; init; }
    public required int Amount { get; init; }
    public required string Label { get; init; }
    public required string Message { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteBool(Ok);
        writer.WriteString(RewardType);
        writer.WriteInteger(Amount);
        writer.WriteString(Label);
        writer.WriteString(Message);
    }
}
