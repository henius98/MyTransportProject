using System.Globalization;
using Microsoft.JSInterop;
using CalendarDateLabels = MyTransportAppWASM.Features.Google.Calendar.CalendarDateLabels;

namespace MyTransportAppWASM.Features.Google.Services;

public sealed class GoogleCalendarWorkflowService(IGoogleWorkspaceService workspace)
{
  public const string MalaysiaHolidayCalendarId = "en.malaysia#holiday@group.v.calendar.google.com";

  private readonly List<GoogleCalendar> _calendars = [];
  private readonly List<GoogleCalendarEvent> _events = [];
  private readonly HashSet<string> _enabledCalendars = [];
  private readonly HashSet<string> _loadedCalendars = [];

  public IReadOnlyList<GoogleCalendar> Calendars => _calendars;
  public IReadOnlyList<GoogleCalendarEvent> Events => _events;
  public IReadOnlySet<string> EnabledCalendars => _enabledCalendars;
  public DateTime DisplayMonth { get; private set; } = new(MalaysiaTime.Today.Year, MalaysiaTime.Today.Month, 1);
  public DateTime SelectedDay { get; private set; } = MalaysiaTime.Today;
  public DateTime LoadedStart { get; private set; }
  public DateTime LoadedEnd { get; private set; }
  public bool Busy { get; private set; }
  public int TaskRefreshVersion { get; private set; }
  public string? Error { get; private set; }
  public string? Status { get; private set; }

  public DateTime GridStart => CalendarDateLabels.MonthGridStart(DisplayMonth);
  public int GridWeekCount => CalendarDateLabels.MonthWeekCount(DisplayMonth);
  public IEnumerable<DateTime> GridDays => Enumerable.Range(0, GridWeekCount * 7).Select(offset => GridStart.AddDays(offset));
  public bool CanCreate => _calendars.Any(IsWritable);

  public bool CanWrite(string calendarId) =>
      _calendars.FirstOrDefault(calendar => calendar.Id == calendarId) is { } cal && IsWritable(cal);

  public bool CanEdit(string calendarId, GoogleCalendarEvent? selectedEvent) =>
      CanWrite(calendarId) &&
      (selectedEvent == null || (!selectedEvent.Locked &&
          (string.IsNullOrEmpty(selectedEvent.EventType) || selectedEvent.EventType == "default")));

  public string DefaultWritableCalendarId() =>
      (_calendars.FirstOrDefault(calendar => calendar.Primary && IsWritable(calendar)) ??
       _calendars.FirstOrDefault(IsWritable))?.Id ?? string.Empty;

  public string CalendarColor(string id)
  {
    var calendar = _calendars.FirstOrDefault(item => item.Id == id);
    return CssColor(calendar?.BackgroundColor, calendar != null && IsHolidayCalendar(calendar) ? "#8983d1" : "#ef6c42");
  }

  public string CalendarTextColor(string id) =>
      CssColor(_calendars.FirstOrDefault(calendar => calendar.Id == id)?.ForegroundColor, "#131314");

  public IEnumerable<GoogleCalendarEvent> EventsOn(DateTime day) => _events
      .Where(entry => _enabledCalendars.Contains(entry.CalendarId) && entry.Start.ParsedEventDate < day.AddDays(1) && entry.End.ParsedEventDate > day)
      .OrderBy(entry => string.IsNullOrEmpty(entry.Start.Date))
      .ThenBy(entry => entry.Start.ParsedEventDate);

  public GoogleCalendarEvent? FindEvent(string calendarId, string eventId) =>
      _events.FirstOrDefault(entry => entry.Id == eventId && entry.CalendarId == calendarId);

  public void SelectDay(DateTime day) => SelectedDay = day;

  public void ClearError() => Error = null;

  public void ClearMessages()
  {
    Error = null;
    Status = null;
  }

  public async Task InitializeAsync(string userId, CancellationToken cancellationToken = default) =>
      await RefreshAsync(userId, incrementTasks: false, cancellationToken);

  public async Task RefreshAsync(string userId, bool incrementTasks = true, CancellationToken cancellationToken = default)
  {
    if (Busy || cancellationToken.IsCancellationRequested) return;
    if (incrementTasks) TaskRefreshVersion++;

    await RunBusyAsync(async () =>
    {
      if (_calendars.Count == 0)
      {
        var result = await workspace.ListCalendarsAsync(userId);
        if (cancellationToken.IsCancellationRequested) return;

        _calendars.AddRange(result);
        if (!_calendars.Any(IsMalaysiaHolidayCalendar))
        {
          _calendars.Add(new()
          {
            Id = MalaysiaHolidayCalendarId,
            Summary = "Holidays in Malaysia",
            AccessRole = "reader",
            BackgroundColor = "#8983d1"
          });
        }

        foreach (var calendar in _calendars.Where(calendar =>
                calendar.Selected || calendar.Primary || IsMalaysiaHolidayCalendar(calendar)))
        {
          _enabledCalendars.Add(calendar.Id);
        }
      }

      var start = GridStart;
      var end = start.AddDays(GridWeekCount * 7);
      _loadedCalendars.Clear();

      var loaded = await Task.WhenAll(
              _calendars.Where(calendar => _enabledCalendars.Contains(calendar.Id))
                  .Select(calendar => LoadCalendarEventsAsync(userId, calendar, start, end)));
      if (cancellationToken.IsCancellationRequested) return;

      _events.Clear();
      foreach (var result in loaded)
      {
        if (result.Error == null)
        {
          _loadedCalendars.Add(result.CalendarId);
          _events.AddRange(result.Events);
        }
      }

      var failures = loaded.Where(result => result.Error != null).Select(result => result.Error).ToList();
      Error = failures.Count > 0 ? string.Join(" ", failures) : null;
      LoadedStart = start;
      LoadedEnd = end;
    }, cancellationToken);
  }

  public async Task ToggleCalendarAsync(string userId, string calendarId, CancellationToken cancellationToken = default)
  {
    if (Busy || cancellationToken.IsCancellationRequested) return;

    if (!_enabledCalendars.Add(calendarId))
    {
      _enabledCalendars.Remove(calendarId);
      return;
    }

    if (_loadedCalendars.Contains(calendarId)) return;

    var calendar = _calendars.FirstOrDefault(item => item.Id == calendarId);
    if (calendar is null) return;

    var start = LoadedStart == default ? GridStart : LoadedStart;
    var end = LoadedEnd == default ? start.AddDays(GridWeekCount * 7) : LoadedEnd;

    await RunBusyAsync(async () =>
    {
      var result = await LoadCalendarEventsAsync(userId, calendar, start, end);
      if (cancellationToken.IsCancellationRequested) return;

      if (result.Error != null)
      {
        Error = result.Error;
        return;
      }

      _loadedCalendars.Add(calendar.Id);
      _events.RemoveAll(entry => entry.CalendarId == calendar.Id);
      _events.AddRange(result.Events);
    }, cancellationToken);
  }

  public async Task SwitchMonthAsync(string userId, DateTime targetDay, CancellationToken cancellationToken = default)
  {
    if (Busy || cancellationToken.IsCancellationRequested) return;

    SelectedDay = targetDay;
    DisplayMonth = new(targetDay.Year, targetDay.Month, 1);
    _events.Clear();
    _loadedCalendars.Clear();
    await RefreshAsync(userId, incrementTasks: false, cancellationToken);
  }

  public async Task<GoogleCalendarEvent?> SaveEventAsync(
      string userId,
      string targetCalendarId,
      GoogleCalendarEvent? existingEvent,
      string title,
      string description,
      string location,
      bool allDay,
      DateTime startsAt,
      DateTime endsAt,
      CancellationToken cancellationToken = default)
  {
    if (Busy || cancellationToken.IsCancellationRequested || !CanEdit(targetCalendarId, existingEvent))
      return null;

    Error = null;
    Status = null;
    var validationError = ValidateEvent(title, allDay, startsAt, endsAt);
    if (validationError != null)
    {
      Error = validationError;
      return null;
    }

    return await RunBusyAsync(async () =>
    {
      var body = BuildEventEdit(title, description, location, allDay, startsAt, endsAt, existingEvent);
      var saved = await workspace.SaveEventAsync(userId, targetCalendarId, existingEvent?.Id, body, existingEvent?.Etag);
      if (cancellationToken.IsCancellationRequested) return null;

      saved.CalendarId = targetCalendarId;
      _events.RemoveAll(entry => entry.Id == saved.Id && entry.CalendarId == targetCalendarId);
      if (saved.Start.ParsedEventDate < LoadedEnd && saved.End.ParsedEventDate > LoadedStart)
        _events.Add(saved);

      _events.Sort((left, right) => DateTime.Compare(left.Start.ParsedEventDate, right.Start.ParsedEventDate));
      Status = "Event saved to Google Calendar.";
      return saved;
    }, cancellationToken);
  }

  public async Task<bool> DeleteEventAsync(
      string userId,
      GoogleCalendarEvent existingEvent,
      CancellationToken cancellationToken = default)
  {
    if (Busy || cancellationToken.IsCancellationRequested || !CanEdit(existingEvent.CalendarId, existingEvent))
      return false;

    return await RunBusyAsync(async () =>
    {
      await workspace.DeleteEventAsync(userId, existingEvent.CalendarId, existingEvent.Id, existingEvent.Etag);
      if (cancellationToken.IsCancellationRequested) return false;

      _events.RemoveAll(entry => entry.Id == existingEvent.Id && entry.CalendarId == existingEvent.CalendarId);
      Status = "Event deleted from Google Calendar.";
      return true;
    }, cancellationToken);
  }

  private async Task<T> RunBusyAsync<T>(
      Func<Task<T>> action,
      CancellationToken cancellationToken,
      T fallback = default!)
  {
    Error = null;
    Status = null;
    Busy = true;
    try
    {
      return await action();
    }
    catch (Exception ex)
    {
      if (!cancellationToken.IsCancellationRequested) Error = ErrorMessage(ex);
      return fallback;
    }
    finally
    {
      Busy = false;
    }
  }

  private Task RunBusyAsync(Func<Task> action, CancellationToken cancellationToken) =>
      RunBusyAsync<object?>(async () =>
      {
        await action();
        return null;
      }, cancellationToken);

  private async Task<(string CalendarId, GoogleCalendarEvent[] Events, string? Error)> LoadCalendarEventsAsync(
      string userId,
      GoogleCalendar calendar,
      DateTime start,
      DateTime end)
  {
    try
    {
      var result = await workspace.ListEventsAsync(userId, calendar.Id, LocalIso(start), LocalIso(end));
      foreach (var entry in result) entry.CalendarId = calendar.Id;
      return (calendar.Id, result, null);
    }
    catch (Exception)
    {
      return (calendar.Id, [], $"Could not load {calendar.Summary}. Refresh or reconnect to try again.");
    }
  }

  public static bool IsWritable(GoogleCalendar calendar) =>
      calendar.AccessRole is "owner" or "writer";

  public static bool IsHolidayCalendar(GoogleCalendar calendar) =>
      calendar.Id.EndsWith("#holiday@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase);

  public static bool IsMalaysiaHolidayCalendar(GoogleCalendar calendar) =>
      calendar.Id.EndsWith(".malaysia#holiday@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase);

  public static string? ValidateEvent(string title, bool allDay, DateTime startsAt, DateTime endsAt)
  {
    if (string.IsNullOrWhiteSpace(title))
      return "Enter an event title.";

    if ((allDay && endsAt.Date < startsAt.Date) || (!allDay && endsAt <= startsAt) || endsAt.Year == 9999)
      return allDay ? "The last day must be on or after the first day." : "The end time must be later than the start time.";

    return null;
  }

  public static GoogleEventEdit BuildEventEdit(
      string title,
      string description,
      string location,
      bool allDay,
      DateTime startsAt,
      DateTime endsAt,
      GoogleCalendarEvent? existingEvent = null) => new()
      {
        Summary = title.Trim(),
        Description = description,
        Location = location,
        Start = allDay
              ? new() { Date = startsAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
              : TimedValue(startsAt, existingEvent?.Start),
        End = allDay
              ? new() { Date = endsAt.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
              : TimedValue(endsAt, existingEvent?.End)
      };

  public static string EventWhen(GoogleCalendarEvent entry)
  {
    var start = entry.Start.ParsedEventDate;
    var end = entry.End.ParsedEventDate;
    if (!string.IsNullOrEmpty(entry.Start.Date))
    {
      var lastDay = end.AddDays(-1);
      return start == lastDay ? $"{start:d} · All day" : $"{start:d} – {lastDay:d} · All day";
    }

    return start.Date == end.Date ? $"{start:g} – {end:t}" : $"{start:g} – {end:g}";
  }

  private static string LocalIso(DateTime value) =>
      MalaysiaTime.At(value).ToString("O", CultureInfo.InvariantCulture);

  private static GoogleEventTime TimedValue(DateTime value, GoogleEventTime? original) =>
      original != null && !string.IsNullOrEmpty(original.DateTime) && original.ParsedEventDate == value
          ? new() { DateTime = original.DateTime, TimeZone = original.TimeZone }
          : new() { DateTime = LocalIso(value) };

  private static string CssColor(string? color, string fallback)
  {
    if (color is not { Length: 7 } || color[0] != '#') return fallback;
    for (var i = 1; i < 7; i++)
    {
      if (!char.IsAsciiHexDigit(color[i])) return fallback;
    }
    return color;
  }

  private static string ErrorMessage(Exception exception) => exception is JSException
      ? exception.Message.Split('\n')[0]
      : "Google Calendar could not complete the request. Your changes are still in the form. Try again, or reconnect your account.";
}
