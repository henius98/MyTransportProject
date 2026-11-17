using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace MyTransportAppWASM.Tests;

public class GoogleCalendarWorkflowServiceTests
{
    private const string Holidays = GoogleCalendarWorkflowService.MalaysiaHolidayCalendarId;

    [Fact]
    public async Task ToggleCalendarAsync_UncheckAndRecheck_DoesNotRefetchOrBumpTaskRefresh()
    {
        var workspace = CreateWorkspaceMock();
        var service = new GoogleCalendarWorkflowService(workspace.Object);

        await service.InitializeAsync("user-1");
        Assert.Equal(0, service.TaskRefreshVersion);
        workspace.Verify(w => w.ListEventsAsync("user-1", "primary", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        workspace.Verify(w => w.ListEventsAsync("user-1", Holidays, It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        // Unchecking a loaded calendar triggers zero network fetches and zero task refreshes.
        await service.ToggleCalendarAsync("user-1", Holidays);
        Assert.DoesNotContain(Holidays, service.EnabledCalendars);
        Assert.Equal(0, service.TaskRefreshVersion);
        workspace.Verify(w => w.ListEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));

        // Re-checking a calendar already loaded for this month reuses in-memory events without re-fetching.
        await service.ToggleCalendarAsync("user-1", Holidays);
        Assert.Contains(Holidays, service.EnabledCalendars);
        Assert.Equal(0, service.TaskRefreshVersion);
        workspace.Verify(w => w.ListEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ToggleCalendarAsync_EnablingUnloadedCalendar_FetchesOnlyThatCalendar()
    {
        var workspace = CreateWorkspaceMock(includeUnselectedWorkCalendar: true);
        var service = new GoogleCalendarWorkflowService(workspace.Object);

        await service.InitializeAsync("user-1");
        Assert.DoesNotContain("work", service.EnabledCalendars);

        await service.ToggleCalendarAsync("user-1", "work");

        Assert.Contains("work", service.EnabledCalendars);
        Assert.Equal(0, service.TaskRefreshVersion);
        workspace.Verify(w => w.ListEventsAsync("user-1", "work", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        workspace.Verify(w => w.ListEventsAsync("user-1", "primary", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        workspace.Verify(w => w.ListEventsAsync("user-1", Holidays, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task RefreshAsync_BumpsTaskRefreshVersion_WhileSwitchMonthDoesNot()
    {
        var workspace = CreateWorkspaceMock();
        var service = new GoogleCalendarWorkflowService(workspace.Object);

        await service.InitializeAsync("user-1");
        Assert.Equal(0, service.TaskRefreshVersion);

        await service.SwitchMonthAsync("user-1", new DateTime(2026, 6, 15));
        Assert.Equal(0, service.TaskRefreshVersion);
        Assert.Equal(new DateTime(2026, 6, 1), service.DisplayMonth);

        await service.RefreshAsync("user-1");
        Assert.Equal(1, service.TaskRefreshVersion);
    }

    [Fact]
    public async Task SaveEventAsync_UsesInclusiveToExclusiveAllDayDatesAndValidatesBounds()
    {
        var workspace = CreateWorkspaceMock();
        workspace.Setup(w => w.SaveEventAsync("user-1", "primary", null, It.IsAny<GoogleEventEdit>(), null))
            .ReturnsAsync((string _, string _, string? _, GoogleEventEdit body, string? _) => new GoogleCalendarEvent
            {
                Id = "created-1",
                Summary = body.Summary ?? "",
                Start = body.Start!,
                End = body.End!
            });

        var service = new GoogleCalendarWorkflowService(workspace.Object);
        await service.InitializeAsync("user-1");

        // Invalid range rejected before calling Workspace.
        var invalid = await service.SaveEventAsync(
            "user-1", "primary", null, "Bad Event", "", "", allDay: true,
            new DateTime(2026, 5, 10), new DateTime(2026, 5, 9));
        Assert.Null(invalid);
        Assert.Equal("The last day must be on or after the first day.", service.Error);

        var startDay = service.LoadedStart.AddDays(1);
        var saved = await service.SaveEventAsync(
            "user-1", "primary", null, "Trip", "Notes", "KL", allDay: true, startDay, startDay.AddDays(1));
        Assert.NotNull(saved);
        Assert.Equal(startDay.ToString("yyyy-MM-dd"), saved!.Start.Date);
        Assert.Equal(startDay.AddDays(2).ToString("yyyy-MM-dd"), saved.End.Date);
        Assert.Contains(service.Events, e => e.Id == "created-1" && e.CalendarId == "primary");
        Assert.Equal("Event saved to Google Calendar.", service.Status);

        service.SelectDay(startDay.AddDays(1));
        Assert.Equal(startDay.AddDays(1), service.SelectedDay);

        service.ClearError();
        Assert.Null(service.Error);

        Assert.True(await service.DeleteEventAsync("user-1", saved));
        Assert.Equal("Event deleted from Google Calendar.", service.Status);
        Assert.DoesNotContain(service.Events, e => e.Id == "created-1");
        Assert.Equal(DateTime.MinValue, GoogleCalendarWorkflowService.EventDate(new GoogleEventTime { Date = "not-a-date" }));
    }

    [Fact]
    public async Task GoogleTaskCountdown_DoesNotRefetchTasks_WhenParametersAreUnchanged()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();
        workspace.Setup(w => w.IsConnectedAsync("user-1", "tasks")).ReturnsAsync(true);
        workspace.Setup(w => w.ListTaskListsAsync("user-1")).ReturnsAsync([new GoogleTaskList { Id = "list-1" }]);
        workspace.Setup(w => w.ListTasksAsync("user-1", "list-1")).ReturnsAsync([new GoogleTask { Id = "t1", Title = "Check feed" }]);

        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(workspace.Object)
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());

        TestCountdownHost? host = null;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<TestCountdownHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["UserId"] = "user-1",
                ["RefreshVersion"] = 0,
                ["OnReady"] = (Action<TestCountdownHost>)(h => host = h)
            }));
            // Re-rendering the parent with unchanged parameters should not trigger additional fetches.
            await host!.TriggerRenderAsync();
        });

        workspace.Verify(w => w.ListTaskListsAsync("user-1"), Times.Once);
        workspace.Verify(w => w.ListTasksAsync("user-1", "list-1"), Times.Once);

        // Bumping RefreshVersion on explicit refresh should trigger a fresh task fetch.
        await renderer.Dispatcher.InvokeAsync(() => host!.BumpRefreshAsync());
        workspace.Verify(w => w.ListTaskListsAsync("user-1"), Times.Exactly(2));
        workspace.Verify(w => w.ListTasksAsync("user-1", "list-1"), Times.Exactly(2));
    }

    private sealed class TestCountdownHost : ComponentBase
    {
        [Parameter] public string UserId { get; set; } = string.Empty;
        [Parameter] public int RefreshVersion { get; set; }
        [Parameter] public Action<TestCountdownHost>? OnReady { get; set; }

        protected override void OnInitialized()
        {
            OnReady?.Invoke(this);
        }

        public Task TriggerRenderAsync() => InvokeAsync(StateHasChanged);

        public Task BumpRefreshAsync()
        {
            RefreshVersion++;
            return InvokeAsync(StateHasChanged);
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<GoogleTaskCountdown>(0);
            builder.AddAttribute(1, "UserId", UserId);
            builder.AddAttribute(2, "RefreshVersion", RefreshVersion);
            builder.CloseComponent();
        }
    }

    private static Mock<IGoogleWorkspaceService> CreateWorkspaceMock(bool includeUnselectedWorkCalendar = false)
    {
        var workspace = new Mock<IGoogleWorkspaceService>();
        List<MyTransportAppWASM.Features.Google.Models.GoogleCalendar> calendars =
        [
            new() { Id = "primary", Summary = "Personal", Primary = true, Selected = true, AccessRole = "owner" }
        ];
        if (includeUnselectedWorkCalendar)
            calendars.Add(new() { Id = "work", Summary = "Work", Primary = false, Selected = false, AccessRole = "writer" });

        workspace.Setup(w => w.ListCalendarsAsync("user-1")).ReturnsAsync(calendars.ToArray());
        workspace.Setup(w => w.ListEventsAsync("user-1", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string calId, string _, string _) =>
            [
                new GoogleCalendarEvent
                {
                    Id = $"evt-{calId}",
                    Summary = $"{calId} event",
                    Start = new() { Date = MalaysiaTime.Today.ToString("yyyy-MM-dd") },
                    End = new() { Date = MalaysiaTime.Today.AddDays(1).ToString("yyyy-MM-dd") }
                }
            ]);

        return workspace;
    }
}
