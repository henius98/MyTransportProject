using System.Text.Json.Serialization;

namespace MyTransportAppWASM.Models;

public sealed class GoogleCalendar
{
    public string Id { get; set; } = "";
    public string Summary { get; set; } = "";
    public string AccessRole { get; set; } = "";
    public bool Primary { get; set; }
    public string TimeZone { get; set; } = "";
}

public sealed class GoogleCalendarEvent
{
    public string Id { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? HtmlLink { get; set; }
    public string Etag { get; set; } = "";
    public string EventType { get; set; } = "default";
    public string? RecurringEventId { get; set; }
    public bool Locked { get; set; }
    public GoogleEventTime Start { get; set; } = new();
    public GoogleEventTime End { get; set; } = new();
}

public sealed class GoogleEventTime
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Date { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZone { get; set; }
}

public sealed class GoogleEventEdit
{
    public string Summary { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Description { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Location { get; set; }

    public GoogleEventTime Start { get; set; } = new();
    public GoogleEventTime End { get; set; } = new();
}

public sealed class GoogleTaskList
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class GoogleTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Notes { get; set; }
    public string? Due { get; set; }
    public string Status { get; set; } = "needsAction";
    public string? Completed { get; set; }
    public string Etag { get; set; } = "";
    public string? Parent { get; set; }
    public string? WebViewLink { get; set; }
    public GoogleTaskAssignmentInfo? AssignmentInfo { get; set; }
}

public sealed class GoogleTaskAssignmentInfo
{
    public string? SurfaceType { get; set; }
    public string? LinkToTask { get; set; }
}

public sealed class GoogleTaskEdit
{
    public string Title { get; set; } = "";

    // Explicit nulls let a PATCH clear optional values on an existing task.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Notes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Due { get; set; }

    public string Status { get; set; } = "needsAction";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Completed { get; set; }
}
