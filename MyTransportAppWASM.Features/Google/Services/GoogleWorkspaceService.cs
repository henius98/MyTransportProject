using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace MyTransportAppWASM.Features.Google.Services;

public sealed class GoogleWorkspaceService(IJSRuntime jsRuntime, FirebaseClientService? firebaseClient = null) : IGoogleWorkspaceService, IAsyncDisposable
{
  private readonly JsModuleLoader _module = new(
      jsRuntime,
      "./js/google-services.js",
      firebaseClient is null ? null : firebaseClient.EnsureConfiguredAsync);

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

  public Task<GoogleTaskExtension[]> ListTaskExtensionsAsync(string uid) =>
      InvokeAsync<GoogleTaskExtension[]>("listTaskExtensions", uid);

  public Task SaveTaskExtensionAsync(string uid, GoogleTaskExtension extension) =>
      InvokeVoidAsync("saveTaskExtension", uid, extension);

  public Task DeleteTaskExtensionsAsync(string uid, GoogleTaskReference[] deleted) =>
      InvokeVoidAsync("deleteTaskExtensions", uid, deleted);

  private async Task<T> InvokeAsync<
      [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors |
          DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
      string identifier, params object?[] args)
  {
    var module = await _module.GetAsync();
    return await module.InvokeAsync<T>(identifier, args);
  }

  private async Task InvokeVoidAsync(string identifier, params object?[] args)
  {
    var module = await _module.GetAsync();
    await module.InvokeVoidAsync(identifier, args);
  }

  public ValueTask DisposeAsync() => _module.DisposeAsync();
}
