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

        script.Should().Contain("[ValidateSet('Daily', 'Interactive', 'Measurement', 'Native', 'All')]");
        script.Should().Contain("Category=DailyE2E");
        script.Should().Contain("Category=Interactive");
        script.Should().Contain("daily.trx");
        script.Should().Contain("interactive.trx");
        script.Should().Contain("GHOSTWIN_AUTOMATION_RUN_REAL_APP");
        script.Should().Contain("GHOSTWIN_INTERACTIVE_AUTOMATION");
        script.Should().Contain("tests\\GhostWin.Automation.Tests\\GhostWin.Automation.Tests.csproj");
        script.Should().NotContain("tests\\GhostWin.E2E.Tests\\GhostWin.E2E.Tests.csproj");
        script.Should().Contain("MeasurementScenario");
        script.Should().Contain("MeasurementRepeatCount");
        script.Should().Contain("measure_render_baseline.ps1");
        script.Should().Contain("measure_render_repeats.ps1");
        script.Should().Contain("measurement");
        script.Should().Contain("Invoke-NativeEngineTests");
    }

    [Fact]
    public void TestAutomationScript_builds_solution_before_real_app_suites()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "test_automation.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("function Invoke-SolutionBuild");
        script.Should().Contain("GhostWin.sln");
        script.Should().Contain("/p:Platform=x64");
        script.Should().Contain("$solutionBuilt");
    }

    [Fact]
    public void TestAutomationScript_defines_active_native_engine_suite()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "test_automation.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("$nativeEngineTests = @(");
        script.Should().Contain("'vt_core_test'");
        script.Should().Contain("'vt_bridge_cell_test'");
        script.Should().Contain("'conpty_integration_test'");
        script.Should().Contain("'dx11_render_test'");
        script.Should().Contain("'render_state_test'");
        script.Should().Contain("'session_visual_state_test'");
        script.Should().Contain("'tsf_init_test'");
        script.Should().Contain("'quad_korean_test'");
        script.Should().NotContain("'vt_minimal_test'");
        script.Should().NotContain("'conpty_benchmark'");
    }

    [Fact]
    public void MeasurementBaselineScript_launches_app_without_inheriting_redirected_stdio()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "measure_render_baseline.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("function Find-MSBuild");
        script.Should().Contain("vswhere.exe");
        script.Should().Contain("& $msbuild");
        script.Should().Contain("function Start-GhostWinApp");
        script.Should().Contain("Start-Process -FilePath $AppExe");
        script.Should().NotContain("UseShellExecute = $false");
        script.Should().Contain("GHOSTWIN_RENDER_PERF");
        script.Should().Contain("GHOSTWIN_LOG_FILE");
    }

    [Fact]
    public void MeasurementBaselineScript_requires_native_dlls_next_to_resolved_app()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "scripts", "measure_render_baseline.ps1");

        File.Exists(scriptPath).Should().BeTrue();
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("function Test-AppRuntimeCandidate");
        script.Should().Contain("ghostwin_engine.dll");
        script.Should().Contain("ghostty-vt.dll");
    }

    [Fact]
    public void MeasurementScripts_include_panecontainer_churn_scenarios()
    {
        var repoRoot = FindRepoRoot();
        var testAutomationPath = Path.Combine(repoRoot, "scripts", "test_automation.ps1");
        var baselinePath = Path.Combine(repoRoot, "scripts", "measure_render_baseline.ps1");

        File.Exists(testAutomationPath).Should().BeTrue();
        File.Exists(baselinePath).Should().BeTrue();

        var testAutomation = File.ReadAllText(testAutomationPath);
        var baseline = File.ReadAllText(baselinePath);

        testAutomation.Should().Contain("pane-split-churn");
        testAutomation.Should().Contain("workspace-switch-churn");
        baseline.Should().Contain("pane-split-churn");
        baseline.Should().Contain("workspace-switch-churn");
        baseline.Should().Contain("--artifact-dir");
    }

    [Fact]
    public void MeasurementBaselineScript_emits_visual_proof_for_panecontainer_churn()
    {
        var repoRoot = FindRepoRoot();
        var baselinePath = Path.Combine(repoRoot, "scripts", "measure_render_baseline.ps1");

        File.Exists(baselinePath).Should().BeTrue();
        var baseline = File.ReadAllText(baselinePath);

        baseline.Should().Contain("function Capture-MeasurementVisualProof");
        baseline.Should().Contain("visual-window.png");
        baseline.Should().Contain("visual-check.json");
        baseline.Should().Contain("visual_valid:");
        baseline.Should().Contain("pane-geometry.json");
        baseline.Should().Contain("workspace-geometry.json");
        baseline.Should().Contain("GetDpiForWindow");
        baseline.Should().Contain("$dpiScale");
        baseline.Should().Contain("ActiveBorderComplete");
        baseline.Should().Contain("TextLikePixels");
        baseline.Should().Contain("$snapshots = Get-Content -LiteralPath $geometryPath -Raw | ConvertFrom-Json");
        baseline.Should().NotContain("$snapshots = @(Get-Content -LiteralPath $geometryPath -Raw | ConvertFrom-Json)");
    }

    [Fact]
    public void MeasurementBaselineScript_normalizes_output_dir_before_launching_app()
    {
        var repoRoot = FindRepoRoot();
        var baselinePath = Path.Combine(repoRoot, "scripts", "measure_render_baseline.ps1");

        File.Exists(baselinePath).Should().BeTrue();
        var baseline = File.ReadAllText(baselinePath);

        baseline.Should().Contain("$OutputDir = (Resolve-Path -LiteralPath $OutputDir).Path");
        baseline.Should().Contain("$logFile       = Join-Path $OutputDir 'ghostwin.log'");
    }

    [Fact]
    public void MeasurementRepeatScript_aggregates_visual_proof_runs()
    {
        var repoRoot = FindRepoRoot();
        var repeatPath = Path.Combine(repoRoot, "scripts", "measure_render_repeats.ps1");
        var testAutomationPath = Path.Combine(repoRoot, "scripts", "test_automation.ps1");

        File.Exists(repeatPath).Should().BeTrue();
        File.Exists(testAutomationPath).Should().BeTrue();

        var repeat = File.ReadAllText(repeatPath);
        var testAutomation = File.ReadAllText(testAutomationPath);

        repeat.Should().Contain("[ValidateRange(1, 20)]");
        repeat.Should().Contain("RepeatCount");
        repeat.Should().Contain("measure_render_baseline.ps1");
        repeat.Should().Contain("repeat-summary.csv");
        repeat.Should().Contain("repeat-summary.json");
        repeat.Should().Contain("visual_active_border_complete");
        repeat.Should().Contain("VisualActiveBorderComplete");
        repeat.Should().Contain("Measurement repeat gate failed");
        testAutomation.Should().Contain("MeasurementRepeatCount -gt 1");
        testAutomation.Should().Contain("measure_render_repeats.ps1");
    }

    [Fact]
    public void GhostWinAppProject_copies_native_dlls_to_target_dir()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "GhostWin.App", "GhostWin.App.csproj");

        File.Exists(projectPath).Should().BeTrue();
        var project = File.ReadAllText(projectPath);

        project.Should().Contain("DestinationFolder=\"$(TargetDir)\"");
        project.Should().NotContain("DestinationFolder=\"$(OutputPath)\"");
    }

    [Fact]
    public void AppXaml_defines_contextual_cursor_affordances()
    {
        var repoRoot = FindRepoRoot();
        var mainWindowPath = Path.Combine(repoRoot, "src", "GhostWin.App", "MainWindow.xaml");
        var palettePath = Path.Combine(repoRoot, "src", "GhostWin.App", "CommandPaletteWindow.xaml");

        File.Exists(mainWindowPath).Should().BeTrue();
        File.Exists(palettePath).Should().BeTrue();

        var mainWindow = File.ReadAllText(mainWindowPath);
        var palette = File.ReadAllText(palettePath);

        mainWindow.Should().Contain("Value=\"Hand\"");
        mainWindow.Should().Contain("Cursor=\"IBeam\"");
        palette.Should().Contain("Cursor=\"IBeam\"");
        palette.Should().Contain("<Setter Property=\"Cursor\" Value=\"Hand\"/>");
    }

    [Fact]
    public void MainWindow_routes_ctrl_tab_through_single_preview_handler()
    {
        var repoRoot = FindRepoRoot();
        var xamlPath = Path.Combine(repoRoot, "src", "GhostWin.App", "MainWindow.xaml");
        var codeBehindPath = Path.Combine(repoRoot, "src", "GhostWin.App", "MainWindow.xaml.cs");

        File.Exists(xamlPath).Should().BeTrue();
        File.Exists(codeBehindPath).Should().BeTrue();

        var xaml = File.ReadAllText(xamlPath);
        var codeBehind = File.ReadAllText(codeBehindPath);

        xaml.Should().NotContain("Gesture=\"Ctrl+Tab\"");
        codeBehind.Should().Contain("PreviewKeyDown += OnTerminalKeyDown");
        codeBehind.Should().Contain("case Key.Tab:");
        codeBehind.Should().Contain("ActivateWorkspace");
    }

    [Fact]
    public void LegacyAutomationInventory_documents_all_cleanup_targets()
    {
        var repoRoot = FindRepoRoot();
        var inventoryPath = Path.Combine(repoRoot, "docs", "03-analysis", "testing", "legacy-automation-inventory.md");

        File.Exists(inventoryPath).Should().BeTrue();
        var inventory = File.ReadAllText(inventoryPath);

        inventory.Should().Contain("tests/GhostWin.E2E.Tests/");
        inventory.Should().Contain("tests/GhostWin.MeasurementDriver/");
        inventory.Should().Contain("tests/e2e-flaui-cross-validation/");
        inventory.Should().Contain("scripts/e2e/e2e_operator/");
        inventory.Should().Contain("scripts/test_m11_cwd_peb.ps1");
        inventory.Should().Contain("scripts/test_korean_");
    }

    [Fact]
    public void DailyAutomationTests_disable_parallel_execution_for_ui_automation()
    {
        var repoRoot = FindRepoRoot();
        var assemblyInfoPath = Path.Combine(repoRoot, "tests", "GhostWin.Automation.Tests", "AssemblyInfo.cs");

        File.Exists(assemblyInfoPath).Should().BeTrue();
        var assemblyInfo = File.ReadAllText(assemblyInfoPath);

        assemblyInfo.Should().Contain("DisableTestParallelization = true");
    }

    [Fact]
    public void Repository_does_not_keep_removed_poc_and_python_runner_paths()
    {
        var repoRoot = FindRepoRoot();
        var removedPaths = new[]
        {
            Path.Combine(repoRoot, "tests", "e2e-flaui-cross-validation"),
            Path.Combine(repoRoot, "tests", "e2e-flaui-split-content"),
            Path.Combine(repoRoot, "tests", "GhostWin.E2E.Tests"),
            Path.Combine(repoRoot, "tests", "GhostWin.MeasurementDriver"),
            Path.Combine(repoRoot, "test_results"),
            Path.Combine(repoRoot, "scripts", "e2e"),
            Path.Combine(repoRoot, "scripts", "e2e", "e2e_operator"),
            Path.Combine(repoRoot, "scripts", "e2e", "venv"),
            Path.Combine(repoRoot, "scripts", "e2e", "runner.py"),
            Path.Combine(repoRoot, "scripts", "e2e", "requirements.txt"),
            Path.Combine(repoRoot, "scripts", "capture_window.py"),
            Path.Combine(repoRoot, "scripts", "run_all_tests.py"),
            Path.Combine(repoRoot, "scripts", "tests"),
            Path.Combine(repoRoot, "scripts", "test_e2e.ps1"),
            Path.Combine(repoRoot, "scripts", "repro_first_pane.ps1"),
            Path.Combine(repoRoot, "scripts", "test_m11_cwd_peb.ps1"),
            Path.Combine(repoRoot, "scripts", "test_m11_e2e_restore.ps1"),
            Path.Combine(repoRoot, "scripts", "test_settings_e2e.ps1"),
            Path.Combine(repoRoot, "scripts", "test_settings_all_e2e.ps1"),
        };

        removedPaths.Where(path => Directory.Exists(path) || File.Exists(path)).Should().BeEmpty();
    }

    [Fact]
    public void ScriptsDirectory_keeps_test_automation_as_the_only_test_entrypoint()
    {
        var repoRoot = FindRepoRoot();
        var scriptsRoot = Path.Combine(repoRoot, "scripts");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "scripts/test_automation.ps1",
        };

        var automationLikeScripts = Directory
            .EnumerateFiles(scriptsRoot, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelativePath(repoRoot, path))
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return path.StartsWith("scripts/e2e/", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("diag_", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("repro_", StringComparison.OrdinalIgnoreCase);
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        automationLikeScripts.Should().BeEquivalentTo(allowed);
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

    private static string NormalizeRelativePath(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
}
