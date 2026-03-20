using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

public sealed class TestPlannerAgent : IAgent
{
    private readonly IArtifactStore _artifactStore;

    public TestPlannerAgent(IArtifactStore artifactStore)
    {
        _artifactStore = artifactStore;
    }

    public string Name => nameof(TestPlannerAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        var symbol = context.Items[PipelineKeys.CurrentSymbol] as string
                     ?? throw new InvalidOperationException("Current symbol not set.");

        var plan = new TestPlan
        {
            TargetSymbol = symbol,
            Cases = new()
            {
                new TestPlanCase
                {
                    Name = "HappyPath",
                    Arrange = "Create target with default collaborators.",
                    Act = "Invoke target with representative valid input.",
                    Assert = "Verify expected output or side effect.",
                    BranchCovered = "normal"
                },
                new TestPlanCase
                {
                    Name = "BoundaryInput",
                    Arrange = "Prepare minimal/edge values.",
                    Act = "Invoke target with boundary input.",
                    Assert = "Verify boundary behavior.",
                    BranchCovered = "boundary"
                },
                new TestPlanCase
                {
                    Name = "ExceptionalPath",
                    Arrange = "Prepare invalid state/input.",
                    Act = "Invoke target and capture exception.",
                    Assert = "Verify exception type/message contract.",
                    BranchCovered = "exception"
                },
                new TestPlanCase
                {
                    Name = "BranchProbe",
                    Arrange = "Set collaborators to trigger alternative branch.",
                    Act = "Invoke target.",
                    Assert = "Verify branch-specific outcome.",
                    BranchCovered = "branch"
                }
            }
        };

        context.Items[PipelineKeys.CurrentPlan] = plan;
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.testplan.json",
            plan,
            cancellationToken);
    }
}
