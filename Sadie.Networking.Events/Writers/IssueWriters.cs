using Sadie.API;
using Sadie.API.Networking;
using Sadie.Networking.Writers.Moderation;
using Sadie.Shared.Attributes;

namespace Sadie.Networking.Events.Writers;

// Live single-issue update sent to moderators (ISSUE_INFO = 3609). Matches the client's
// IssueInfoMessageParser field-for-field (15 scalar fields + a pattern-match list).
[PacketId(3609)]
public class IssueInfoWriter : AbstractPacketWriter
{
    public required IssueData Issue { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer)
    {
        writer.WriteInteger(Issue.IssueId);
        writer.WriteInteger(Issue.State);
        writer.WriteInteger(Issue.CategoryId);
        writer.WriteInteger(Issue.ReportedCategoryId);
        writer.WriteInteger(Issue.IssueAgeInMs);
        writer.WriteInteger(Issue.Priority);
        writer.WriteInteger(Issue.GroupingId);
        writer.WriteInteger(Issue.ReporterUserId);
        writer.WriteString(Issue.ReporterUsername ?? "");
        writer.WriteInteger(Issue.ReportedUserId);
        writer.WriteString(Issue.ReportedUsername ?? "");
        writer.WriteInteger(Issue.PickerUserId);
        writer.WriteString(Issue.PickerUsername ?? "");
        writer.WriteString(Issue.Message ?? "");
        writer.WriteInteger(Issue.ChatRecordId);

        var patterns = Issue.Patterns ?? [];
        writer.WriteInteger(patterns.Count);

        foreach (var pattern in patterns)
        {
            writer.WriteString(pattern.Pattern ?? "");
            writer.WriteInteger(pattern.StartIndex);
            writer.WriteInteger(pattern.EndIndex);
        }
    }
}

// Issue removed from the queue (ISSUE_DELETED = 3192). The client parser does parseInt(readString()),
// so the id MUST be written as a string.
[PacketId(3192)]
public class IssueDeletedWriter : AbstractPacketWriter
{
    public required int IssueId { get; init; }

    public override void OnSerialize(INetworkPacketWriter writer) => writer.WriteString(IssueId.ToString());
}

// NOTE: ISSUE_PICK_FAILED (3150) is intentionally NOT a writer here — that outgoing id clashes with
// the incoming PlayerInventoryFurnitureItems handler (3150), and Sadie registers handlers + writers
// in one PacketId map. Pick-failure feedback is sent as a normal PlayerAlertWriter instead.
