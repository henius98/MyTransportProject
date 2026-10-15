using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Services.Interfaces;

public record GoogleTaskTreeResult
{
    public IReadOnlyList<GoogleTaskList> TaskLists { get; init; } = Array.Empty<GoogleTaskList>();
    public IReadOnlyDictionary<string, List<GoogleTask>> TasksByList { get; init; } = new Dictionary<string, List<GoogleTask>>();
    public IReadOnlyDictionary<string, GoogleTask> TaskLookup { get; init; } = new Dictionary<string, GoogleTask>();
    public IReadOnlyDictionary<string, GoogleTaskExtension> Extensions { get; init; } = new Dictionary<string, GoogleTaskExtension>();
}

public record GoogleTaskSaveRequest
{
    public string? TaskId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public DateTime? DueDate { get; init; }
    public bool IsCompleted { get; init; }
    public string? CompletedTimestamp { get; init; }
    public string? Etag { get; init; }
    public int? EstimatedMinutes { get; init; }
    public IReadOnlyList<GoogleTaskReference> Dependencies { get; init; } = Array.Empty<GoogleTaskReference>();
}

public record GoogleTaskDeleteResult
{
    public IReadOnlyList<GoogleTask> RemainingTasks { get; init; } = Array.Empty<GoogleTask>();
    public IReadOnlyList<GoogleTaskReference> DeletedReferences { get; init; } = Array.Empty<GoogleTaskReference>();
}

public sealed class GoogleTaskExtensionSyncException : InvalidOperationException
{
    public GoogleTask SavedTask { get; }

    public GoogleTaskExtensionSyncException(GoogleTask savedTask, string message, Exception innerException)
        : base(message, innerException)
    {
        SavedTask = savedTask;
    }
}

public interface IGoogleTasksWorkflowService
{
    Task<GoogleTaskTreeResult> LoadTaskTreeAsync(string userId, CancellationToken cancellationToken = default);

    Task<GoogleTask> SaveTaskWithWorkflowAsync(
        string userId,
        string listId,
        GoogleTaskSaveRequest request,
        CancellationToken cancellationToken = default);

    Task<GoogleTaskDeleteResult> DeleteTaskWithWorkflowAsync(
        string userId,
        string listId,
        string taskId,
        string? etag = null,
        CancellationToken cancellationToken = default);

    Task<GoogleTaskExtension> StartTaskAsync(
        string userId,
        string listId,
        string taskId,
        CancellationToken cancellationToken = default);

    Task<GoogleTask> ToggleTaskCompletionAsync(
        string userId,
        string listId,
        GoogleTask task,
        CancellationToken cancellationToken = default);
}


