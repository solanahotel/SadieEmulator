using Sadie.API;
using Sadie.API.Networking;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Moderator "user info" panel response (MODERATION_USER_INFO = 2866). Field order matches the
// client's ModeratorUserInfoData constructor exactly. Counts we don't persist (cfh / caution) are
// sent as 0; the trade-lock fields are driven by the in-memory TradeLockStore.
[PacketId(2866)]
public class ModeratorUserInfoWriter : AbstractPacketWriter
{
    public required int UserId { get; init; }
    public required string Username { get; init; }
    public required string Figure { get; init; }
    public required int RegistrationAgeInMinutes { get; init; }
    public required int MinutesSinceLastLogin { get; init; }
    public required bool Online { get; init; }
    public int CfhCount { get; init; }
    public int AbusiveCfhCount { get; init; }
    public int CautionCount { get; init; }
    public int BanCount { get; init; }
    public int TradingLockCount { get; init; }
    public string TradingExpiryDate { get; init; } = "";
    public string LastPurchaseDate { get; init; } = "";
    public int IdentityId { get; init; }
    public int IdentityRelatedBanCount { get; init; }
    public string PrimaryEmailAddress { get; init; } = "";
    public string UserClassification { get; init; } = "";

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(UserId);
        writer.WriteString(Username);
        writer.WriteString(Figure);
        writer.WriteInteger(RegistrationAgeInMinutes);
        writer.WriteInteger(MinutesSinceLastLogin);
        writer.WriteBool(Online);
        writer.WriteInteger(CfhCount);
        writer.WriteInteger(AbusiveCfhCount);
        writer.WriteInteger(CautionCount);
        writer.WriteInteger(BanCount);
        writer.WriteInteger(TradingLockCount);
        writer.WriteString(TradingExpiryDate);
        writer.WriteString(LastPurchaseDate);
        writer.WriteInteger(IdentityId);
        writer.WriteInteger(IdentityRelatedBanCount);
        writer.WriteString(PrimaryEmailAddress);
        writer.WriteString(UserClassification);
    }
}
