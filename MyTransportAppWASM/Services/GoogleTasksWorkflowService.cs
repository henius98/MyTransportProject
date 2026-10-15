using System.Globalization;
using MyTransportAppWASM.Models;
using MyTransportAppWASM.Services.Interfaces;

namespace MyTransportAppWASM.Services;

public class GoogleTasksWorkflowService : IGoogleTasksWorkflowService
{
    private readonly IGoogleWorkspaceService _workspace;

    public GoogleTasksWorkflowService(IGoogleWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    public async Task<GoogleTaskTreeResult> LoadTaskTreeAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        var lists = await _workspace.ListTaskListsAsync(userId);
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = await Task.WhenAll(lists.Select(async list =>
            (list.Id, Tasks: (await _workspace.ListTasksAsync(userId, list.Id)).ToList())));
        cancellationToken.ThrowIfCancellationRequested();
        var loadedExtensions = await _workspace.ListTaskExtensionsAsync(userId);
        cancellationToken.ThrowIfCancellationRequested();

        var taskLists = lists.ToList();
        var tasksByList = loaded.ToDictionary(result => result.Id, result => result.Tasks);
        var taskLookup = tasksByList
            .SelectMany(kv => kv.Value.Select(task => (ListId: kv.Key, Task: task)))
            .ToDictionary(item => GoogleTaskWorkflow.Key(item.ListId, item.Task.Id), item => item.Task);
        var extensions = loadedExtensions
            .ToDictionary(ext => GoogleTaskWorkflow.Key(ext.TaskListId, ext.TaskId));

        // Reconcile and prune dangling extensions
        var missing = loadedExtensions
            .Select(ext => new GoogleTaskReference { TaskListId = ext.TaskListId, TaskId = ext.TaskId })
            .Concat(loadedExtensions.SelectMany(ext => ext.DependsOn))
            .Where(reference => !taskLookup.ContainsKey(GoogleTaskWorkflow.Key(reference)))
            .DistinctBy(GoogleTaskWorkflow.Key).ToArray();

        if (missing.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _workspace.DeleteTaskExtensionsAsync(userId, missing);
            GoogleTaskWorkflow.RemoveReferences(extensions, missing);
        }

        return new GoogleTaskTreeResult
        {
            TaskLists = taskLists,
            TasksByList = tasksByList,
            TaskLookup = taskLookup,
            Extensions = extensions
        };
    }

    public async Task<GoogleTask> SaveTaskWithWorkflowAsync(
        string userId,
        string listId,
        GoogleTaskSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Enter a task title.");
        }

        if (request.EstimatedMinutes is < 1 or > 1440)
        {
            throw new ArgumentOutOfRangeException(null, "Enter an estimated duration between 1 and 1440 minutes.");
        }

        if (request.Dependencies.Count > 100)
        {
            throw new ArgumentException("Choose at most 100 dependency tasks.");
        }

        if (request.TaskId != null && request.Dependencies.Any(dep => dep.TaskListId == listId && dep.TaskId == request.TaskId))
        {
            throw new InvalidOperationException("Choose existing tasks other than this task as dependencies.");
        }

        var latestExtensions = (await _workspace.ListTaskExtensionsAsync(userId))
            .ToDictionary(ext => GoogleTaskWorkflow.Key(ext.TaskListId, ext.TaskId));
        cancellationToken.ThrowIfCancellationRequested();

        if (request.TaskId != null && GoogleTaskWorkflow.CreatesCycle(
            GoogleTaskWorkflow.Key(listId, request.TaskId), request.Dependencies, latestExtensions))
        {
            throw new InvalidOperationException("These dependencies would create a circular task chain.");
        }

        var edit = new GoogleTaskEdit
        {
            Title = request.Title.Trim(),
            Notes = request.Notes,
            Due = request.DueDate?.ToString("yyyy-MM-dd'T'00:00:00.000'Z'", CultureInfo.InvariantCulture),
            Status = request.IsCompleted ? "completed" : "needsAction",
            Completed = request.IsCompleted
                ? request.CompletedTimestamp ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                : null
        };

        var savedTask = await _workspace.SaveTaskAsync(userId, listId, request.TaskId, edit, request.Etag);

        var key = GoogleTaskWorkflow.Key(listId, savedTask.Id);
        var extension = new GoogleTaskExtension
        {
            TaskListId = listId,
            TaskId = savedTask.Id,
            EstimatedMinutes = request.EstimatedMinutes,
            DependsOn = request.Dependencies.ToList(),
            StartedAt = latestExtensions.GetValueOrDefault(key)?.StartedAt
        };

        if (extension.EstimatedMinutes != null || extension.DependsOn.Count > 0 ||
            extension.StartedAt != null || latestExtensions.ContainsKey(key))
        {
            try
            {
                await _workspace.SaveTaskExtensionAsync(userId, extension);
            }
            catch (Exception ex)
            {
                throw new GoogleTaskExtensionSyncException(
                    savedTask,
                    "Task saved to Google Tasks, but its extension details did not sync. Retry Save.",
                    ex);
            }
        }

        return savedTask;
    }

    public async Task<GoogleTaskDeleteResult> DeleteTaskWithWorkflowAsync(
        string userId,
        string listId,
        string taskId,
        string? etag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();

        var previousTasks = (await _workspace.ListTasksAsync(userId, listId)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await _workspace.DeleteTaskAsync(userId, listId, taskId, etag);
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = (await _workspace.ListTasksAsync(userId, listId)).ToList();
        remaining.RemoveAll(task => task.Id == taskId);
        var remainingIds = remaining.Select(task => task.Id).ToHashSet();

        var deleted = previousTasks.Where(task => !remainingIds.Contains(task.Id))
            .Select(task => new GoogleTaskReference { TaskListId = listId, TaskId = task.Id })
            .Append(new GoogleTaskReference { TaskListId = listId, TaskId = taskId })
            .DistinctBy(GoogleTaskWorkflow.Key).ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        await _workspace.DeleteTaskExtensionsAsync(userId, deleted);

        return new GoogleTaskDeleteResult
        {
            RemainingTasks = remaining,
            DeletedReferences = deleted
        };
    }

    public async Task<GoogleTaskExtension> StartTaskAsync(
        string userId,
        string listId,
        string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();

        var key = GoogleTaskWorkflow.Key(listId, taskId);
        var loadedExtensions = await _workspace.ListTaskExtensionsAsync(userId);
        cancellationToken.ThrowIfCancellationRequested();
        var ext = loadedExtensions.FirstOrDefault(item => item.TaskListId == listId && item.TaskId == taskId);

        var requiredListIds = (ext?.DependsOn.Select(dep => dep.TaskListId) ?? [])
            .Append(listId)
            .Distinct()
            .ToArray();

        var loadedTasks = await Task.WhenAll(requiredListIds.Select(async id =>
            (ListId: id, Tasks: await _workspace.ListTasksAsync(userId, id))));
        cancellationToken.ThrowIfCancellationRequested();

        var taskLookup = loadedTasks
            .SelectMany(item => item.Tasks.Select(task => (item.ListId, Task: task)))
            .DistinctBy(item => GoogleTaskWorkflow.Key(item.ListId, item.Task.Id))
            .ToDictionary(item => GoogleTaskWorkflow.Key(item.ListId, item.Task.Id), item => item.Task);

        if (!taskLookup.TryGetValue(key, out var task))
        {
            throw new KeyNotFoundException("This task was not found.");
        }

        if (GoogleTaskWorkflow.State(task, ext, taskLookup) != GoogleTaskWorkflowState.Ready)
        {
            throw new InvalidOperationException("This task is no longer ready. Complete its prerequisites first.");
        }

        var updated = new GoogleTaskExtension
        {
            TaskListId = listId,
            TaskId = taskId,
            EstimatedMinutes = ext?.EstimatedMinutes,
            DependsOn = ext?.DependsOn.ToList() ?? [],
            StartedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        cancellationToken.ThrowIfCancellationRequested();
        await _workspace.SaveTaskExtensionAsync(userId, updated);
        return updated;
    }

    public async Task<GoogleTask> ToggleTaskCompletionAsync(
        string userId,
        string listId,
        GoogleTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();

        var isComplete = task.Status != "completed";
        var edit = new GoogleTaskEdit
        {
            Title = task.Title,
            Notes = task.Notes,
            Due = task.Due,
            Status = isComplete ? "completed" : "needsAction",
            Completed = isComplete ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) : null
        };

        return await _workspace.SaveTaskAsync(userId, listId, task.Id, edit, task.Etag);
    }
}

