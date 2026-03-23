namespace CodeBrain.Workflows;

public static class PipelineKeys
{
    public const string RepoMap = "repo.map";
    public const string CurrentSymbol = "pipeline.currentSymbol";
    public const string CurrentCard = "pipeline.currentCard";
    public const string CurrentDrilldown = "pipeline.currentDrilldown";
    public const string CurrentChangeScope = "pipeline.currentChangeScope";
    public const string CurrentEditPlan = "pipeline.currentEditPlan";
    public const string CurrentPlan = "pipeline.currentPlan";
    public const string LatestTestRun = "pipeline.latestTestRun";
    public const string LatestCoverage = "pipeline.latestCoverage";
    public const string TestProjectPath = "pipeline.testProject";
    public const string LastBlocker = "pipeline.lastBlocker";
}
