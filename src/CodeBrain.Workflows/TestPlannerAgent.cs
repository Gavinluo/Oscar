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
        var scope = context.Items[PipelineKeys.CurrentChangeScope] as RepositoryChangeScope;
        var editPlan = context.Items[PipelineKeys.CurrentEditPlan] as RepositoryEditPlan;

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
                    Arrange = scope is null
                        ? "Set collaborators to trigger alternative branch."
                        : $"Set collaborators or neighboring symbols to cover scoped dependencies: {string.Join(", ", scope.RelatedSymbols.Take(3))}.",
                    Act = "Invoke target.",
                    Assert = "Verify branch-specific outcome.",
                    BranchCovered = "branch"
                }
            }
        };

        if (editPlan is not null && editPlan.VerificationSteps.Count > 0)
        {
            plan.Cases.Add(new TestPlanCase
            {
                Name = "ScopedVerification",
                Arrange = $"Focus on scoped edit targets: {string.Join(", ", editPlan.SymbolsToEdit.Take(3))}.",
                Act = "Invoke the path touched by the proposed edit.",
                Assert = $"Verify the scoped behavior and rerun: {string.Join(", ", editPlan.VerificationSteps.Take(3))}.",
                BranchCovered = "scope"
            });
        }

        context.Items[PipelineKeys.CurrentPlan] = plan;
        await _artifactStore.WriteJsonAsync(
            $"plans/{_artifactStore.GetSafeArtifactName(symbol)}.testplan.json",
            plan,
            cancellationToken);
    }
}
