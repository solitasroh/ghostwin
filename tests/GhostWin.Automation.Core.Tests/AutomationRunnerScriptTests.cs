using FluentAssertions;

namespace GhostWin.Automation.Core.Tests;

public sealed class AutomationRunnerScriptTests
{
    [Fact]
    public void TestAutomationScript_defines_separate_daily_and_interactive_filters()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "test_automation.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("Category=DailyE2E");
        script.Should().Contain("Category=Interactive");
        script.Should().Contain("daily.trx");
        script.Should().Contain("interactive.trx");
        script.Should().Contain("GHOSTWIN_AUTOMATION_RUN_REAL_APP");
        script.Should().Contain("GHOSTWIN_INTERACTIVE_AUTOMATION");
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GhostWin.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("GhostWin.sln was not found above the test output directory.");
    }
}
