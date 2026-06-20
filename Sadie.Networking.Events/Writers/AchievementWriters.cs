using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Events.Achievements;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Outgoing achievement packets. Field orders mirror the Nitro client parsers exactly:
//   - AchievementData (used by the list 305 and the single update 2107)
//   - AchievementLevelUpData (the unlock notification 806)
// The achievement score uses the compiled PlayerAchievementScoreWriter (1968), not redefined here.

// Full achievements list (ACHIEVEMENT_LIST = 305 incoming): count, then each achievement, then the
// default category string.
[PacketId(305)]
public class AchievementsListWriter : AbstractPacketWriter
{
    public required IReadOnlyList<AchievementView> Achievements { get; init; }
    public string DefaultCategory { get; init; } = "";

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Achievements.Count);

        foreach (var achievement in Achievements)
        {
            WriteAchievement(writer, achievement);
        }

        writer.WriteString(DefaultCategory);
    }

    // Matches the client AchievementData constructor field-for-field.
    internal static void WriteAchievement(INetworkPacketWriter writer, AchievementView a)
    {
        writer.WriteInteger(a.AchievementId);
        writer.WriteInteger(a.Level);
        writer.WriteString(a.BadgeCode);
        writer.WriteInteger(a.ScoreAtStartOfLevel);
        writer.WriteInteger(a.ScoreLimit);
        writer.WriteInteger(a.LevelRewardPoints);
        writer.WriteInteger(a.LevelRewardPointType);
        writer.WriteInteger(a.CurrentPoints);
        writer.WriteBool(a.FinalLevel);
        writer.WriteString(a.Category);
        writer.WriteString(a.SubCategory);
        writer.WriteInteger(a.LevelCount);
        writer.WriteInteger(a.DisplayMethod);
    }
}

// Single achievement update (ACHIEVEMENT_PROGRESSED = 2107 incoming): one AchievementData. Sent
// whenever a player's progress on one achievement changes so the panel refreshes live.
[PacketId(2107)]
public class AchievementUpdateWriter : AbstractPacketWriter
{
    public required AchievementView Achievement { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer) =>
        AchievementsListWriter.WriteAchievement(writer, Achievement);
}

// Unlock notification (ACHIEVEMENT_NOTIFICATION = 806 incoming): the celebratory level-up dialog.
// Matches the client AchievementLevelUpData constructor.
[PacketId(806)]
public class AchievementNotificationWriter : AbstractPacketWriter
{
    public required int CategoryType { get; init; }
    public required int Level { get; init; }
    public int BadgeId { get; init; }
    public required string BadgeCode { get; init; }
    public int Points { get; init; }
    public int LevelRewardPoints { get; init; }
    public int LevelRewardPointType { get; init; }
    public int BonusPoints { get; init; }
    public required int AchievementId { get; init; }
    public string RemovedBadgeCode { get; init; } = "";
    public required string Category { get; init; }
    public bool ShowDialogToUser { get; init; } = true;

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(CategoryType);
        writer.WriteInteger(Level);
        writer.WriteInteger(BadgeId);
        writer.WriteString(BadgeCode);
        writer.WriteInteger(Points);
        writer.WriteInteger(LevelRewardPoints);
        writer.WriteInteger(LevelRewardPointType);
        writer.WriteInteger(BonusPoints);
        writer.WriteInteger(AchievementId);
        writer.WriteString(RemovedBadgeCode);
        writer.WriteString(Category);
        writer.WriteBool(ShowDialogToUser);
    }
}
