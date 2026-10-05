using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Pages.Google;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Utils;

namespace MyTransportAppWASM.Tests;

public class GoogleCalendarRenderingTests
{
    private const string Holidays = "en.malaysia#holiday@group.v.calendar.google.com";

    [Fact]
    public async Task PersonalEventsAndMalaysianHolidaysRenderTogetherEvenWithIdenticalEventIds()
    {
        var (html, module) = await RenderAsync();

        Assert.Contains("Personal appointment", html);
        Assert.Contains("Malaysia holiday fixture", html);
        Assert.Contains("Holidays in Malaysia", html);
        Assert.Contains("农历", html);
        Assert.Contains("gcal-week-number", html);
        Assert.Contains("Next task", html);
        Assert.Contains("Time until midnight in Malaysia", html);
        Assert.True(html.IndexOf("End of today", StringComparison.Ordinal) < html.IndexOf("Next task", StringComparison.Ordinal));
        Assert.DoesNotContain("gcal-mini", html);
        module.Verify(js => js.InvokeAsync<GoogleCalendarEvent[]>("listEvents",
            It.Is<object?[]>(args => Equals(args[1], "primary"))), Times.Once);
        module.Verify(js => js.InvokeAsync<GoogleCalendarEvent[]>("listEvents",
            It.Is<object?[]>(args => Equals(args[1], Holidays))), Times.Once);
    }

    [Fact]
    public async Task HolidayFailureKeepsPersonalEventsVisibleAndShowsRetryMessage()
    {
        var (html, _) = await RenderAsync(failHolidays: true);

        Assert.Contains("Personal appointment", html);
        Assert.Contains("Could not load Holidays in Malaysia", html);
        Assert.DoesNotContain("Malaysia holiday fixture", html);
    }

    [Fact]
    public async Task ExistingHolidaySubscriptionIsNotDuplicated()
    {
        var (_, module) = await RenderAsync(subscribedHolidays: true);

        module.Verify(js => js.InvokeAsync<GoogleCalendarEvent[]>("listEvents",
            It.Is<object?[]>(args => Equals(args[1], Holidays))), Times.Once);
    }

    [Fact]
    public async Task CountdownSelectsNearestIncompleteDatedTaskAcrossLists()
    {
        var (html, _) = await RenderAsync(taskGroups:
        [
            [new() { Title = "Later task", Due = DueIn(5) },
             new() { Title = "Completed task", Due = DueIn(0), Status = "completed" }],
            [new() { Title = "Nearest task", Due = DueIn(2) },
             new() { Title = "Overdue task", Due = DueIn(-1) },
             new() { Title = "Undated task" }]
        ]);

        Assert.Contains("Nearest task", html);
        Assert.Contains("Time until task due date", html);
        Assert.DoesNotContain("Later task", html);
        Assert.DoesNotContain("Completed task", html);
        Assert.DoesNotContain("Overdue task", html);
        Assert.DoesNotContain("Undated task", html);
    }

    [Fact]
    public async Task CountdownShowsDueTodayForDateOnlyTask()
    {
        var (html, _) = await RenderAsync(taskGroups: [[new() { Title = "Today task", Due = DueIn(0) }]]);

        Assert.Contains("Today task", html);
        Assert.Contains("Due today", html);
        Assert.DoesNotContain("Time until task due date", html);
        Assert.Contains("Time until midnight in Malaysia", html);
    }

    [Fact]
    public async Task CountdownShowsEmptyStateWhenNoUpcomingDatedTasksExist()
    {
        var (html, _) = await RenderAsync(taskGroups: [[
            new() { Due = DueIn(-1) }, new() { Due = "invalid" }, new(),
            new() { Due = DueIn(2), Status = "completed" }
        ]]);

        Assert.Contains("No upcoming tasks", html);
        Assert.DoesNotContain("Time until task due date", html);
        Assert.Contains("Time until midnight in Malaysia", html);
    }

    [Fact]
    public async Task CountdownOffersTasksConnectionWithoutLoadingTaskListsWhenDisconnected()
    {
        var (html, module) = await RenderAsync(tasksConnected: false);

        Assert.Contains("Connect Google Tasks", html);
        Assert.Contains("Time until midnight in Malaysia", html);
        Assert.Contains("Personal appointment", html);
        module.Verify(js => js.InvokeAsync<GoogleTaskList[]>("listTaskLists", It.IsAny<object?[]>()), Times.Never);
    }

    [Fact]
    public async Task TaskFailureKeepsCalendarVisibleAndShowsRetryMessage()
    {
        var (html, _) = await RenderAsync(failTasks: true, taskGroups: [[]]);

        Assert.Contains("Could not load tasks", html);
        Assert.Contains("Time until midnight in Malaysia", html);
        Assert.Contains("Personal appointment", html);
        Assert.DoesNotContain("No upcoming tasks", html);
    }

    private static string DueIn(int days) => MalaysiaTime.Today.AddDays(days).ToString("yyyy-MM-dd'T'00:00:00.000'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<(string Html, Mock<IJSObjectReference> Module)> RenderAsync(bool failHolidays = false, bool subscribedHolidays = false,
        GoogleTask[][]? taskGroups = null, bool tasksConnected = true, bool failTasks = false)
    {
        var runtime = new Mock<IJSRuntime>();
        var module = new Mock<IJSObjectReference>();
        runtime.Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>())).ReturnsAsync(module.Object);
        module.Setup(js => js.InvokeAsync<bool>("isConnected", It.Is<object?[]>(args => Equals(args[1], "tasks"))))
            .ReturnsAsync(tasksConnected);
        var groups = taskGroups ?? [];
        module.Setup(js => js.InvokeAsync<GoogleTaskList[]>("listTaskLists", It.IsAny<object?[]>()))
            .ReturnsAsync(groups.Select((_, index) => new GoogleTaskList { Id = index.ToString() }).ToArray());
        module.Setup(js => js.InvokeAsync<GoogleTask[]>("listTasks", It.IsAny<object?[]>()))
            .Returns((string _, object?[]? args) => failTasks
                ? ValueTask.FromException<GoogleTask[]>(new JSException("Offline"))
                : ValueTask.FromResult(groups[int.Parse((string)args![1]!)]));
        List<Models.GoogleCalendar> calendars = [new() { Id = "primary", Summary = "Personal", Primary = true, AccessRole = "owner" }];
        if (subscribedHolidays)
            calendars.Add(new() { Id = Holidays, Summary = "Holidays in Malaysia", AccessRole = "reader" });
        module.Setup(js => js.InvokeAsync<Models.GoogleCalendar[]>("listCalendars", It.IsAny<object?[]>())).ReturnsAsync(calendars.ToArray());
        module.Setup(js => js.InvokeAsync<GoogleCalendarEvent[]>("listEvents", It.IsAny<object?[]>()))
            .Returns((string _, object?[]? args) =>
            {
                var isHoliday = Equals(args![1], Holidays);
                if (isHoliday && failHolidays) return ValueTask.FromException<GoogleCalendarEvent[]>(new JSException("Offline"));
                return ValueTask.FromResult<GoogleCalendarEvent[]>([new()
                {
                    Id = "same-id", Summary = isHoliday ? "Malaysia holiday fixture" : "Personal appointment",
                    Start = new() { Date = MalaysiaTime.Today.ToString("yyyy-MM-dd") },
                    End = new() { Date = MalaysiaTime.Today.AddDays(1).ToString("yyyy-MM-dd") }
                }]);
            });
        module.Setup(js => js.DisposeAsync()).Returns(ValueTask.CompletedTask);
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(runtime.Object)
            .AddSingleton<GoogleWorkspaceService>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<GoogleCalendarContent>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["UserId"] = "test-user" }));
            return output.ToHtmlString();
        });
        return (WebUtility.HtmlDecode(html), module);
    }
}
