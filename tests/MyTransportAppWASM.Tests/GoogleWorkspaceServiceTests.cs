using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;

namespace MyTransportAppWASM.Tests;

public class GoogleWorkspaceServiceTests
{
    [Fact]
    public async Task ModuleIsLoadedOnDemandAndSharedByConcurrentRequests()
    {
        var import = new TaskCompletionSource<IJSObjectReference>();
        var runtime = new Mock<IJSRuntime>(MockBehavior.Strict);
        var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
        runtime.Setup(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(new ValueTask<IJSObjectReference>(import.Task));
        module.Setup(js => js.InvokeAsync<bool>("isConnected", It.IsAny<object?[]>()))
            .ReturnsAsync(true);
        module.Setup(js => js.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var service = new GoogleWorkspaceService(runtime.Object);
        runtime.VerifyNoOtherCalls();

        var calendar = service.IsConnectedAsync("alice", "calendar");
        var tasks = service.IsConnectedAsync("alice", "tasks");
        runtime.Verify(js => js.InvokeAsync<IJSObjectReference>("import",
            It.Is<object?[]>(args => Equals(args[0], "./js/google-services.js"))), Times.Once);
        Assert.False(calendar.IsCompleted);
        Assert.False(tasks.IsCompleted);

        import.SetResult(module.Object);
        Assert.True(await calendar);
        Assert.True(await tasks);
        await service.DisposeAsync();
        module.Verify(js => js.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task FailedModuleImportCanRetryAfterReconnecting()
    {
        var runtime = new Mock<IJSRuntime>(MockBehavior.Strict);
        var module = new Mock<IJSObjectReference>(MockBehavior.Strict);
        runtime.SetupSequence(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()))
            .Returns(ValueTask.FromException<IJSObjectReference>(new JSException("Offline")))
            .Returns(ValueTask.FromResult(module.Object));
        module.Setup(js => js.InvokeAsync<bool>("isConnected", It.IsAny<object?[]>()))
            .ReturnsAsync(true);
        module.Setup(js => js.DisposeAsync()).Returns(ValueTask.CompletedTask);
        await using var service = new GoogleWorkspaceService(runtime.Object);

        await Assert.ThrowsAsync<JSException>(() => service.IsConnectedAsync("alice", "calendar"));
        Assert.True(await service.IsConnectedAsync("alice", "calendar"));
        Assert.True(await service.IsConnectedAsync("alice", "tasks"));

        runtime.Verify(js => js.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]>()), Times.Exactly(2));
    }

    [Fact]
    public void TaskEditRetainsNullsToClearExistingFieldsAndReopenCompletedTasks()
    {
        var edit = new GoogleTaskEdit { Title = "Buy tickets", Status = "needsAction" };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(edit, InteropOptions));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("notes").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("due").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("completed").ValueKind);
        Assert.Equal("needsAction", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EventEditKeepsDateOnlyAndTimestampValuesDistinctAndClearsOptionalFields(bool allDay)
    {
        var edit = new GoogleEventEdit
        {
            Summary = "Visit Penang",
            Start = allDay
                ? new GoogleEventTime { Date = "2026-09-12" }
                : new GoogleEventTime { DateTime = "2026-09-12T09:00:00+08:00" },
            End = allDay
                ? new GoogleEventTime { Date = "2026-09-13" }
                : new GoogleEventTime { DateTime = "2026-09-12T10:00:00+08:00" }
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(edit, InteropOptions));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("location").ValueKind);
        foreach (var field in new[] { "start", "end" })
        {
            var time = json.RootElement.GetProperty(field);
            Assert.True(time.TryGetProperty(allDay ? "date" : "dateTime", out _));
            Assert.False(time.TryGetProperty(allDay ? "dateTime" : "date", out _));
            Assert.False(time.TryGetProperty("timeZone", out _));
        }
    }

    private static readonly JsonSerializerOptions InteropOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
