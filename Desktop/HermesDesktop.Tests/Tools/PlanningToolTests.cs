using Hermes.Agent.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HermesDesktop.Tests.Tools;

[TestClass]
public sealed class PlanningToolTests
{
    [TestMethod]
    public void Metadata_ExposesPlanningTool()
    {
        var tool = new PlanningTool();

        Assert.AreEqual("planning", tool.Name);
        Assert.AreEqual(typeof(PlanningParameters), tool.ParametersType);
        StringAssert.Contains(tool.Description, "plans");
    }

    [TestMethod]
    public async Task CreateThenGet_ReturnsPlanWithNotStartedSteps()
    {
        var tool = new PlanningTool();

        var create = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "create",
            PlanId = "ship-feature",
            Title = "Ship feature",
            Steps = new[] { "Inspect contracts", "Implement tool", "Add tests" }
        }, CancellationToken.None);

        Assert.IsTrue(create.Success, create.Content);
        StringAssert.Contains(create.Content, "Plan created: ship-feature");
        StringAssert.Contains(create.Content, "0. [ ] Inspect contracts");

        var get = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "get",
            PlanId = "ship-feature"
        }, CancellationToken.None);

        Assert.IsTrue(get.Success, get.Content);
        StringAssert.Contains(get.Content, "Plan: Ship feature (ID: ship-feature)");
        StringAssert.Contains(get.Content, "Progress: 0/3 completed");
    }

    [TestMethod]
    public async Task MarkStep_UpdatesStatusAndNotes()
    {
        var tool = new PlanningTool();
        await CreateSamplePlan(tool);

        var result = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "mark_step",
            PlanId = "sample",
            StepIndex = 1,
            StepStatus = "blocked",
            StepNotes = "Waiting on an API decision."
        }, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
        StringAssert.Contains(result.Content, "1. [!] Second");
        StringAssert.Contains(result.Content, "Notes: Waiting on an API decision.");
        StringAssert.Contains(result.Content, "1 blocked");
    }

    [TestMethod]
    public async Task MarkStep_UsesActivePlan_WhenPlanIdIsOmitted()
    {
        var tool = new PlanningTool();
        await CreateSamplePlan(tool);

        var result = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "mark_step",
            StepIndex = 0,
            StepStatus = "in_progress"
        }, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
        StringAssert.Contains(result.Content, "0. [~] First");
    }

    [TestMethod]
    public async Task List_ShowsAllPlansWithProgress()
    {
        var tool = new PlanningTool();
        await CreateSamplePlan(tool);
        await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "create",
            PlanId = "second",
            Title = "Second plan",
            Steps = new[] { "Only step" }
        }, CancellationToken.None);

        await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "mark_step",
            PlanId = "sample",
            StepIndex = 0,
            StepStatus = "completed"
        }, CancellationToken.None);

        var list = await tool.ExecuteAsync(new PlanningParameters { Command = "list" }, CancellationToken.None);

        Assert.IsTrue(list.Success, list.Content);
        StringAssert.Contains(list.Content, "- sample (active): Sample plan - 1/2 completed");
        StringAssert.Contains(list.Content, "- second: Second plan - 0/1 completed");
    }

    [TestMethod]
    public async Task Delete_RemovesPlan()
    {
        var tool = new PlanningTool();
        await CreateSamplePlan(tool);

        var delete = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "delete",
            PlanId = "sample"
        }, CancellationToken.None);

        Assert.IsTrue(delete.Success, delete.Content);

        var get = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "get",
            PlanId = "sample"
        }, CancellationToken.None);

        Assert.IsFalse(get.Success);
        StringAssert.Contains(get.Content, "No plan found");
    }

    [TestMethod]
    public async Task InvalidStatus_Fails()
    {
        var tool = new PlanningTool();
        await CreateSamplePlan(tool);

        var result = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "mark_step",
            PlanId = "sample",
            StepIndex = 0,
            StepStatus = "waiting"
        }, CancellationToken.None);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Content, "Invalid step_status");
    }

    [TestMethod]
    public async Task State_IsPerToolInstance()
    {
        var first = new PlanningTool();
        var second = new PlanningTool();

        await CreateSamplePlan(first);

        var firstList = await first.ExecuteAsync(new PlanningParameters { Command = "list" }, CancellationToken.None);
        var secondList = await second.ExecuteAsync(new PlanningParameters { Command = "list" }, CancellationToken.None);

        StringAssert.Contains(firstList.Content, "sample");
        Assert.AreEqual("No plans available.", secondList.Content);
    }

    [TestMethod]
    public async Task ActionAlias_IsAccepted()
    {
        var tool = new PlanningTool();

        var result = await tool.ExecuteAsync(new PlanningParameters
        {
            Action = "create",
            PlanId = "alias",
            Title = "Alias plan",
            Steps = new[] { "Use action alias" }
        }, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
        StringAssert.Contains(result.Content, "Plan created: alias");
    }

    private static async Task CreateSamplePlan(PlanningTool tool)
    {
        var result = await tool.ExecuteAsync(new PlanningParameters
        {
            Command = "create",
            PlanId = "sample",
            Title = "Sample plan",
            Steps = new[] { "First", "Second" }
        }, CancellationToken.None);

        Assert.IsTrue(result.Success, result.Content);
    }
}
