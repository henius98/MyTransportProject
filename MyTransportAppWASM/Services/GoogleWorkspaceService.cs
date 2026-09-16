using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;
using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services;

public sealed class GoogleWorkspaceService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private Task<IJSObjectReference>? _moduleTask;

    public Task ConnectAsync(string uid, string service) =>
        InvokeVoidAsync("connect", uid, service);

    public Task<bool> IsConnectedAsync(string uid, string service) =>
        InvokeAsync<bool>("isConnected", uid, service);

    public Task<GoogleCalendar[]> ListCalendarsAsync(string uid) =>
        InvokeAsync<GoogleCalendar[]>("listCalendars", uid);

    public Task<GoogleCalendarEvent[]> ListEventsAsync(
        string uid, string calendarId, string startIso, string endIso) =>
        InvokeAsync<GoogleCalendarEvent[]>("listEvents", uid, calendarId, startIso, endIso);

    public Task<GoogleCalendarEvent> SaveEventAsync(
        string uid, string calendarId, string? eventId, GoogleEventEdit body, string? etag = null) =>
        InvokeAsync<GoogleCalendarEvent>("saveEvent", uid, calendarId, eventId, body, etag);

    public Task DeleteEventAsync(string uid, string calendarId, string eventId, string? etag = null) =>
        InvokeVoidAsync("deleteEvent", uid, calendarId, eventId, etag);

    public Task<GoogleTaskList[]> ListTaskListsAsync(string uid) =>
        InvokeAsync<GoogleTaskList[]>("listTaskLists", uid);

    public Task<GoogleTask[]> ListTasksAsync(string uid, string listId) =>
        InvokeAsync<GoogleTask[]>("listTasks", uid, listId);

    public Task<GoogleTask> SaveTaskAsync(
        string uid, string listId, string? taskId, GoogleTaskEdit body, string? etag = null) =>
        InvokeAsync<GoogleTask>("saveTask", uid, listId, taskId, body, etag);

    public Task DeleteTaskAsync(string uid, string listId, string taskId, string? etag = null) =>
        InvokeVoidAsync("deleteTask", uid, listId, taskId, etag);

    private Task<IJSObjectReference> GetModuleAsync()
    {
        if (_moduleTask is null || _moduleTask.IsFaulted || _moduleTask.IsCanceled)
            _moduleTask = jsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/google-services.js").AsTask();

        return _moduleTask;
    }

    private async Task<T> InvokeAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string identifier, params object?[] args)
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<T>(identifier, args);
    }

    private async Task InvokeVoidAsync(string identifier, params object?[] args)
    {
        var module = await GetModuleAsync();
        await module.InvokeVoidAsync(identifier, args);
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is null) return;

        try
        {
            var module = await _moduleTask;
            await module.DisposeAsync();
        }
        catch (JSException)
        {
            // Loading may have failed while offline, or the browser may be closing.
        }
        catch (JSDisconnectedException)
        {
            // Normal during browser teardown.
        }
    }
}
