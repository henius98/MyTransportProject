using Moq;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Tests;

public class GoogleTasksWorkflowServiceTests
{
    private const string UserId = "user-123";
    private const string ListId = "list-1";

    [Fact]
    public async Task LoadTaskTreeAsync_ReconcilesOrphanedExtensionsAndBuildsLookup()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.ListTaskListsAsync(UserId))
            .ReturnsAsync([new GoogleTaskList { Id = ListId, Title = "My Tasks" }]);

        workspace.Setup(w => w.ListTasksAsync(UserId, ListId))
            .ReturnsAsync([
                new GoogleTask { Id = "task-1", Title = "Task 1" }
            ]);

        workspace.Setup(w => w.ListTaskExtensionsAsync(UserId))
            .ReturnsAsync([
                new GoogleTaskExtension { TaskListId = ListId, TaskId = "task-1", EstimatedMinutes = 30 },
                new GoogleTaskExtension { TaskListId = ListId, TaskId = "deleted-task", EstimatedMinutes = 15 } // dangling
            ]);

        var service = new GoogleTasksWorkflowService(workspace.Object);

        var tree = await service.LoadTaskTreeAsync(UserId);

        Assert.Single(tree.TaskLists);
        Assert.Single(tree.TasksByList[ListId]);
        Assert.Single(tree.Extensions);
        Assert.True(tree.Extensions.ContainsKey(GoogleTaskWorkflow.Key(ListId, "task-1")));
        Assert.False(tree.Extensions.ContainsKey(GoogleTaskWorkflow.Key(ListId, "deleted-task")));

        // Verified orphaned extension was pruned
        workspace.Verify(w => w.DeleteTaskExtensionsAsync(
            UserId,
            It.Is<GoogleTaskReference[]>(refs => refs.Any(r => r.TaskId == "deleted-task"))), Times.Once);
    }

    [Fact]
    public async Task SaveTaskWithWorkflowAsync_RejectsInvalidInput()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();
        var service = new GoogleTasksWorkflowService(workspace.Object);

        // Empty title
        var titleEx = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, new GoogleTaskSaveRequest { Title = "" }));
        Assert.Equal("Enter a task title.", titleEx.Message);

        // Invalid estimated minutes
        var minRangeEx = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, new GoogleTaskSaveRequest { Title = "Task", EstimatedMinutes = 0 }));
        Assert.Equal("Enter an estimated duration between 1 and 1440 minutes.", minRangeEx.Message);
        var maxRangeEx = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, new GoogleTaskSaveRequest { Title = "Task", EstimatedMinutes = 1500 }));
        Assert.Equal("Enter an estimated duration between 1 and 1440 minutes.", maxRangeEx.Message);

        // Self-dependency
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, new GoogleTaskSaveRequest
            {
                TaskId = "task-1",
                Title = "Task",
                Dependencies = [new GoogleTaskReference { TaskListId = ListId, TaskId = "task-1" }]
            }));
    }

    [Fact]
    public async Task SaveTaskWithWorkflowAsync_RejectsCircularDependency()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.ListTaskExtensionsAsync(UserId))
            .ReturnsAsync([
                new GoogleTaskExtension
                {
                    TaskListId = ListId,
                    TaskId = "task-b",
                    DependsOn = [new GoogleTaskReference { TaskListId = ListId, TaskId = "task-a" }]
                }
            ]);

        var service = new GoogleTasksWorkflowService(workspace.Object);

        // task-a depends on task-b -> cycle
        var request = new GoogleTaskSaveRequest
        {
            TaskId = "task-a",
            Title = "Task A",
            Dependencies = [new GoogleTaskReference { TaskListId = ListId, TaskId = "task-b" }]
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, request));

        Assert.Contains("circular task chain", ex.Message);
        workspace.Verify(w => w.SaveTaskAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GoogleTaskEdit>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveTaskWithWorkflowAsync_SavesBothGoogleTaskAndExtension()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.ListTaskExtensionsAsync(UserId))
            .ReturnsAsync([]);

        workspace.Setup(w => w.SaveTaskAsync(UserId, ListId, "task-1", It.IsAny<GoogleTaskEdit>(), "etag-1"))
            .ReturnsAsync(new GoogleTask { Id = "task-1", Title = "Updated Task" });

        var service = new GoogleTasksWorkflowService(workspace.Object);

        var request = new GoogleTaskSaveRequest
        {
            TaskId = "task-1",
            Title = "Updated Task",
            EstimatedMinutes = 45,
            Etag = "etag-1"
        };

        var result = await service.SaveTaskWithWorkflowAsync(UserId, ListId, request);

        Assert.Equal("task-1", result.Id);
        workspace.Verify(w => w.SaveTaskAsync(UserId, ListId, "task-1", It.IsAny<GoogleTaskEdit>(), "etag-1"), Times.Once);
        workspace.Verify(w => w.SaveTaskExtensionAsync(UserId, It.Is<GoogleTaskExtension>(ext =>
            ext.TaskId == "task-1" && ext.EstimatedMinutes == 45)), Times.Once);
    }

    [Fact]
    public async Task SaveTaskWithWorkflowAsync_WhenExtensionSaveFails_PreservesSavedGoogleTaskInException()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.ListTaskExtensionsAsync(UserId))
            .ReturnsAsync([]);

        var savedGoogleTask = new GoogleTask { Id = "new-task-1", Title = "Created Task", Etag = "etag-fresh" };
        workspace.Setup(w => w.SaveTaskAsync(UserId, ListId, null, It.IsAny<GoogleTaskEdit>(), null))
            .ReturnsAsync(savedGoogleTask);

        workspace.Setup(w => w.SaveTaskExtensionAsync(UserId, It.IsAny<GoogleTaskExtension>()))
            .ThrowsAsync(new InvalidOperationException("Firestore unavailable"));

        var service = new GoogleTasksWorkflowService(workspace.Object);

        var request = new GoogleTaskSaveRequest
        {
            TaskId = null,
            Title = "Created Task",
            EstimatedMinutes = 30
        };

        var ex = await Assert.ThrowsAsync<GoogleTaskExtensionSyncException>(() =>
            service.SaveTaskWithWorkflowAsync(UserId, ListId, request));

        Assert.Same(savedGoogleTask, ex.SavedTask);
        Assert.Contains("extension details did not sync", ex.Message);
    }

    [Fact]
    public async Task DeleteTaskWithWorkflowAsync_DeletesFromGoogleAndCleansUpExtensions()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        var parent = new GoogleTask { Id = "parent" };
        var subtask = new GoogleTask { Id = "subtask", Parent = "parent" };
        var survivor = new GoogleTask { Id = "survivor" };

        workspace.SetupSequence(w => w.ListTasksAsync(UserId, ListId))
            .ReturnsAsync([parent, subtask, survivor])
            .ReturnsAsync([survivor]); // after deletion, parent and subtask are gone

        var service = new GoogleTasksWorkflowService(workspace.Object);

        var result = await service.DeleteTaskWithWorkflowAsync(UserId, ListId, "parent", "etag-p");

        Assert.Single(result.RemainingTasks);
        Assert.Equal("survivor", result.RemainingTasks[0].Id);
        Assert.Equal(2, result.DeletedReferences.Count);
        workspace.Verify(w => w.DeleteTaskAsync(UserId, ListId, "parent", "etag-p"), Times.Once);
        // Both parent and cascaded subtask extensions cleaned up
        workspace.Verify(w => w.DeleteTaskExtensionsAsync(UserId, It.Is<GoogleTaskReference[]>(refs =>
            refs.Length == 2 && refs.Any(r => r.TaskId == "parent") && refs.Any(r => r.TaskId == "subtask"))), Times.Once);
    }

    [Fact]
    public async Task StartTaskAsync_ValidatesReadinessBeforeStarting_WithoutFetchingAllTaskLists()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.ListTasksAsync(UserId, ListId))
            .ReturnsAsync([
                new GoogleTask { Id = "task-blocked", Status = "needsAction" },
                new GoogleTask { Id = "prerequisite", Status = "needsAction" }
            ]);

        workspace.Setup(w => w.ListTaskExtensionsAsync(UserId))
            .ReturnsAsync([
                new GoogleTaskExtension
                {
                    TaskListId = ListId,
                    TaskId = "task-blocked",
                    DependsOn = [new GoogleTaskReference { TaskListId = ListId, TaskId = "prerequisite" }]
                }
            ]);

        var service = new GoogleTasksWorkflowService(workspace.Object);

        // Prerequisite is not complete -> task is Blocked
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartTaskAsync(UserId, ListId, "task-blocked"));

        Assert.Contains("no longer ready", ex.Message);
        workspace.Verify(w => w.ListTaskListsAsync(It.IsAny<string>()), Times.Never);
        workspace.Verify(w => w.SaveTaskExtensionAsync(It.IsAny<string>(), It.IsAny<GoogleTaskExtension>()), Times.Never);
    }

    [Fact]
    public async Task ToggleTaskCompletionAsync_TogglesStatusAndTimestamp()
    {
        var workspace = new Mock<IGoogleWorkspaceService>();

        workspace.Setup(w => w.SaveTaskAsync(UserId, ListId, "task-1", It.Is<GoogleTaskEdit>(e => e.Status == "completed" && e.Completed != null), It.IsAny<string?>()))
            .ReturnsAsync(new GoogleTask { Id = "task-1", Status = "completed" });

        var service = new GoogleTasksWorkflowService(workspace.Object);

        var task = new GoogleTask { Id = "task-1", Status = "needsAction", Title = "Test" };
        var completed = await service.ToggleTaskCompletionAsync(UserId, ListId, task);

        Assert.NotNull(completed);
        Assert.Equal("completed", completed.Status);
        workspace.Verify(w => w.SaveTaskAsync(UserId, ListId, "task-1", It.IsAny<GoogleTaskEdit>(), It.IsAny<string?>()), Times.Once);
    }
}
