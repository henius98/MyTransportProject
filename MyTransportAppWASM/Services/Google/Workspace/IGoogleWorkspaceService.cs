using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces;

public interface IGoogleWorkspaceService
{
    Task ConnectAsync(string uid, string service);
    Task<bool> IsConnectedAsync(string uid, string service);
    Task<GoogleCalendar[]> ListCalendarsAsync(string uid);
    Task<GoogleCalendarEvent[]> ListEventsAsync(string uid, string calendarId, string startIso, string endIso);
    Task<GoogleCalendarEvent> SaveEventAsync(string uid, string calendarId, string? eventId, GoogleEventEdit body, string? etag = null);
    Task DeleteEventAsync(string uid, string calendarId, string eventId, string? etag = null);
    Task<GoogleTaskList[]> ListTaskListsAsync(string uid);
    Task<GoogleTask[]> ListTasksAsync(string uid, string listId);
    Task<GoogleTask> SaveTaskAsync(string uid, string listId, string? taskId, GoogleTaskEdit body, string? etag = null);
    Task DeleteTaskAsync(string uid, string listId, string taskId, string? etag = null);
    Task<GoogleTaskExtension[]> ListTaskExtensionsAsync(string uid);
    Task SaveTaskExtensionAsync(string uid, GoogleTaskExtension extension);
    Task DeleteTaskExtensionsAsync(string uid, GoogleTaskReference[] deleted);
}

