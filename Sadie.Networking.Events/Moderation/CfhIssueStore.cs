using System.Collections.Concurrent;
using Sadie.Networking.Writers.Moderation;

namespace Sadie.Networking.Events.Moderation;

// In-memory queue of Call-for-Help tickets. The emulator shipped with the mod-tool init packet
// (ModToolsWriter) but no issue handling, so submitted tickets went nowhere. This store holds the
// open/picked issues; the login init reads it, and the CFH handlers push live updates to mods.
public class CfhIssue
{
    public int IssueId { get; init; }
    public int DbId { get; set; } // row id in cfh_tickets, for the durable audit trail
    public int State { get; set; } = 1; // 1 = open, 2 = picked
    public int ReportedCategoryId { get; init; }
    public DateTime CreatedAt { get; init; }
    public int ReporterUserId { get; init; }
    public string ReporterUsername { get; init; } = "";
    public int ReportedUserId { get; init; }
    public string ReportedUsername { get; set; } = "";
    public int PickerUserId { get; set; }
    public string PickerUsername { get; set; } = "";
    public string Message { get; init; } = "";
    public int RoomId { get; init; }

    public IssueData ToIssueData() => new()
    {
        IssueId = IssueId,
        State = State,
        CategoryId = 1,
        ReportedCategoryId = ReportedCategoryId,
        IssueAgeInMs = (int) Math.Max(0, (DateTime.Now - CreatedAt).TotalMilliseconds),
        Priority = 1,
        GroupingId = -1,
        ReporterUserId = ReporterUserId,
        ReporterUsername = ReporterUsername,
        ReportedUserId = ReportedUserId,
        ReportedUsername = ReportedUsername,
        PickerUserId = PickerUserId,
        PickerUsername = PickerUsername,
        Message = Message,
        ChatRecordId = 0,
        Patterns = []
    };
}

public static class CfhIssueStore
{
    private static int _nextId;
    private static readonly ConcurrentDictionary<int, CfhIssue> Issues = new();

    public static CfhIssue Add(int reporterId, string reporterName, int reportedId, string reportedName,
        int categoryId, string message, int roomId)
    {
        var id = Interlocked.Increment(ref _nextId);

        var issue = new CfhIssue
        {
            IssueId = id,
            State = 1,
            ReportedCategoryId = categoryId,
            CreatedAt = DateTime.Now,
            ReporterUserId = reporterId,
            ReporterUsername = reporterName,
            ReportedUserId = reportedId,
            ReportedUsername = reportedName,
            Message = message,
            RoomId = roomId
        };

        Issues[id] = issue;
        return issue;
    }

    public static bool TryGet(int id, out CfhIssue issue) => Issues.TryGetValue(id, out issue!);
    public static void Remove(int id) => Issues.TryRemove(id, out _);
    public static IReadOnlyCollection<CfhIssue> GetAll() => Issues.Values.ToList();

    // The cfh_tickets row id of the ticket a moderator is currently handling (picked, not yet
    // released/closed), or null. Used to stamp the originating ticket onto sanctions they issue
    // while holding it. Most-recently-picked wins if somehow holding more than one.
    public static int? GetActiveTicketDbIdForMod(long modId)
    {
        var issue = Issues.Values
            .Where(x => x.State == 2 && x.PickerUserId == (int) modId && x.DbId > 0)
            .OrderByDescending(x => x.IssueId)
            .FirstOrDefault();

        return issue?.DbId;
    }
}
