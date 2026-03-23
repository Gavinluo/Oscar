using System.Text;
using CodeBrain.Core.Abstractions;
using CodeBrain.Core.Models;

namespace CodeBrain.Workflows;

public sealed class TestWriterAgent : IAgent
{
    private readonly IArtifactStore _artifactStore;

    public TestWriterAgent(IArtifactStore artifactStore)
    {
        _artifactStore = artifactStore;
    }

    public string Name => nameof(TestWriterAgent);

    public async Task ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        if (context.Items[PipelineKeys.CurrentPlan] is not TestPlan plan)
        {
            throw new InvalidOperationException("Test plan missing in pipeline context.");
        }
        var editPlan = context.Items[PipelineKeys.CurrentEditPlan] as RepositoryEditPlan;

        var testProjectPath = context.Items.TryGetValue(PipelineKeys.TestProjectPath, out var testProjObj)
            ? testProjObj as string
            : null;
        if (string.IsNullOrWhiteSpace(testProjectPath))
        {
            throw new InvalidOperationException("Test project path is not configured.");
        }

        var testProjectDir = Path.GetDirectoryName(testProjectPath)!;
        var generatedDir = Path.Combine(testProjectDir, "Generated");
        Directory.CreateDirectory(generatedDir);
        var className = $"{Sanitize(plan.TargetSymbol)}Tests";
        var filePath = Path.Combine(generatedDir, $"{className}.cs");

        var sb = new StringBuilder();
        sb.AppendLine("using NUnit.Framework;");
        sb.AppendLine();
        sb.AppendLine("namespace AgenticGeneratedTests;");
        sb.AppendLine();
        sb.AppendLine("[TestFixture]");
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        if (editPlan is not null)
        {
            sb.AppendLine($"    // Edit goal: {EscapeComment(editPlan.Goal)}");
            sb.AppendLine($"    // Verification: {EscapeComment(string.Join("; ", editPlan.VerificationSteps.Take(3)))}");
            sb.AppendLine();
        }
        foreach (var testCase in plan.Cases)
        {
            sb.AppendLine("    [Test]");
            sb.AppendLine($"    public void {Sanitize(testCase.Name)}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        // Arrange: {EscapeComment(testCase.Arrange)}");
            sb.AppendLine($"        // Act: {EscapeComment(testCase.Act)}");
            sb.AppendLine($"        // Assert: {EscapeComment(testCase.Assert)}");
            sb.AppendLine("        Assert.Pass(\"Generated placeholder test. Replace with concrete assertions.\");");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        sb.AppendLine("}");

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
        await _artifactStore.WriteLogAsync(
            $"logs/test_writer.{Sanitize(plan.TargetSymbol)}.md",
            $"Generated NUnit test scaffold: {filePath}",
            cancellationToken);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Generated" : new string(chars);
    }

    private static string EscapeComment(string value) => value.Replace(Environment.NewLine, " ").Replace("*/", "* /");
}
