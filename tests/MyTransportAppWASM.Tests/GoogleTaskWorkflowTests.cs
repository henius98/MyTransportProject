using MyTransportAppWASM.Models;

namespace MyTransportAppWASM.Tests;

public class GoogleTaskWorkflowTests
{
    private static GoogleTaskReference Ref(string id, string listId = "list") => new() { TaskListId = listId, TaskId = id };
    private static GoogleTaskExtension Extension(string id, params GoogleTaskReference[] dependencies) =>
        new() { TaskListId = "list", TaskId = id, DependsOn = dependencies.ToList() };
    private static string Key(string id, string listId = "list") => GoogleTaskWorkflow.Key(listId, id);

    [Fact]
    public void ChainAndMultipleDependenciesRecomputeFromGoogleCompletion()
    {
        var a = new GoogleTask { Id = "a" };
        var b = new GoogleTask { Id = "b" };
        var c = new GoogleTask { Id = "c" };
        var tasks = new Dictionary<string, GoogleTask> { [Key("a")] = a, [Key("b")] = b, [Key("c")] = c };
        var bExtension = Extension("b", Ref("a"));
        var cExtension = Extension("c", Ref("a"), Ref("b"));

        Assert.Equal(GoogleTaskWorkflowState.Ready, GoogleTaskWorkflow.State(a, null, tasks));
        Assert.Equal(GoogleTaskWorkflowState.Blocked, GoogleTaskWorkflow.State(b, bExtension, tasks));
        Assert.Equal(2, GoogleTaskWorkflow.BlockingTasks(cExtension, tasks).Count);
        a.Status = "completed";
        Assert.Equal(GoogleTaskWorkflowState.Ready, GoogleTaskWorkflow.State(b, bExtension, tasks));
        Assert.Single(GoogleTaskWorkflow.BlockingTasks(cExtension, tasks));
        b.Status = "completed";
        Assert.Equal(GoogleTaskWorkflowState.Ready, GoogleTaskWorkflow.State(c, cExtension, tasks));
        c.Status = "completed";
        Assert.Equal(GoogleTaskWorkflowState.Completed, GoogleTaskWorkflow.State(c, cExtension, tasks));
    }

    [Fact]
    public void StartedTaskIsInProgressAndMissingPrerequisitesBlockIt()
    {
        var task = new GoogleTask { Id = "b" };
        var tasks = new Dictionary<string, GoogleTask> { [Key("b")] = task };
        var extension = Extension("b", Ref("removed"));
        extension.StartedAt = "2026-09-23T00:00:00Z";
        Assert.Equal(GoogleTaskWorkflowState.Blocked, GoogleTaskWorkflow.State(task, extension, tasks));
        extension.DependsOn.Clear();
        Assert.Equal(GoogleTaskWorkflowState.InProgress, GoogleTaskWorkflow.State(task, extension, tasks));
    }

    [Fact]
    public void RejectsSelfCyclesAndIndirectCyclesAcrossLists()
    {
        var extensions = new Dictionary<string, GoogleTaskExtension>
        {
            [Key("b")] = Extension("b", Ref("a")),
            [Key("c")] = Extension("c", Ref("b"))
        };
        Assert.True(GoogleTaskWorkflow.CreatesCycle(Key("a"), [Ref("a")], extensions));
        Assert.True(GoogleTaskWorkflow.CreatesCycle(Key("a"), [Ref("c")], extensions));
        Assert.True(GoogleTaskWorkflow.CreatesCycle(Key("a"), [Ref("c"), Ref("other", "elsewhere")], extensions));
        Assert.False(GoogleTaskWorkflow.CreatesCycle(Key("c"), [Ref("a")], extensions));
        Assert.NotEqual(Key("same", "list"), Key("same", "elsewhere"));
        extensions[Key("outer", "elsewhere")] = new GoogleTaskExtension
            { TaskListId = "elsewhere", TaskId = "outer", DependsOn = [Ref("a")] };
        Assert.True(GoogleTaskWorkflow.CreatesCycle(Key("a"), [Ref("outer", "elsewhere")], extensions));
    }

    [Theory]
    [InlineData(15, "15 min")]
    [InlineData(30, "30 min")]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 hr")]
    [InlineData(90, "1 hr 30 min")]
    [InlineData(120, "2 hr")]
    public void FormatsDuration(int minutes, string expected) =>
        Assert.Equal(expected, GoogleTaskWorkflow.FormatDuration(minutes));
}
