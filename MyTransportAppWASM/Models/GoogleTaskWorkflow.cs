namespace MyTransportAppWASM.Models;

public enum GoogleTaskWorkflowState { Blocked, Ready, InProgress, Completed }

public static class GoogleTaskWorkflow
{
    public static string Key(string listId, string taskId) => $"{listId}\0{taskId}";

    public static string Key(GoogleTaskReference reference) => Key(reference.TaskListId, reference.TaskId);

    public static IReadOnlyList<GoogleTaskReference> BlockingTasks(
        GoogleTaskExtension? extension, IReadOnlyDictionary<string, GoogleTask> tasks)
        => (extension?.DependsOn ?? []).Where(reference =>
            !tasks.TryGetValue(Key(reference), out var task) || task.Status != "completed").ToArray();

    public static GoogleTaskWorkflowState State(GoogleTask task, GoogleTaskExtension? extension,
        IReadOnlyDictionary<string, GoogleTask> tasks)
    {
        if (task.Status == "completed") return GoogleTaskWorkflowState.Completed;
        if (BlockingTasks(extension, tasks).Count > 0) return GoogleTaskWorkflowState.Blocked;
        return extension?.StartedAt != null ? GoogleTaskWorkflowState.InProgress : GoogleTaskWorkflowState.Ready;
    }

    public static bool CreatesCycle(string taskKey, IEnumerable<GoogleTaskReference> dependencies,
        IReadOnlyDictionary<string, GoogleTaskExtension> extensions)
    {
        var visited = new HashSet<string>();
        bool ReachesTask(string key)
        {
            if (key == taskKey) return true;
            if (!visited.Add(key) || !extensions.TryGetValue(key, out var extension)) return false;
            return extension.DependsOn.Any(reference => ReachesTask(Key(reference)));
        }
        return dependencies.Any(reference => ReachesTask(Key(reference)));
    }

    public static string FormatDuration(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        var hours = minutes / 60;
        var remaining = minutes % 60;
        return remaining == 0 ? $"{hours} hr" : $"{hours} hr {remaining} min";
    }
}
