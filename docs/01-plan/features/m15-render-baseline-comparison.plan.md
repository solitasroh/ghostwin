# M-15 Stage A Internal Baseline Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automate GhostWin internal baseline collection for `idle`, `resize-4pane`, and `load` so M-14 Known Gaps G1/G4/G5 are closed with reproducible artifacts.

**Architecture:** Keep [measure_render_baseline.ps1](C:/Users/Solit/Rootech/works/ghostwin/scripts/measure_render_baseline.ps1) as the single entrypoint for launch, log capture, CSV conversion, CPU capture, and artifact layout. Add a small `tests/GhostWin.MeasurementDriver` console helper for UI automation only: foregrounding the window, creating 4 panes, typing the fixed load workload, and returning a JSON validity contract that the script can merge into `summary.txt`.

**Tech Stack:** PowerShell 7, .NET 10, FlaUI UIA3, Win32 `user32.dll`, xUnit, FluentAssertions, `typeperf`, optional PresentMon.

---

## Scope Check

This plan covers **Stage A only** from [m15-render-baseline-comparison.design.md](C:/Users/Solit/Rootech/works/ghostwin/docs/02-design/features/m15-render-baseline-comparison.design.md).  
Stage B (WT / WezTerm / Alacritty comparison) depends on Stage A artifact format being stable and should be planned separately after Stage A completes.

---

## File Map

### Create

- `tests/GhostWin.MeasurementDriver/GhostWin.MeasurementDriver.csproj`
- `tests/GhostWin.MeasurementDriver/Program.cs`
- `tests/GhostWin.MeasurementDriver/Contracts/DriverOptions.cs`
- `tests/GhostWin.MeasurementDriver/Contracts/DriverResult.cs`
- `tests/GhostWin.MeasurementDriver/Infrastructure/Win32.cs`
- `tests/GhostWin.MeasurementDriver/Infrastructure/MainWindowFinder.cs`
- `tests/GhostWin.MeasurementDriver/Infrastructure/GhostWinController.cs`
- `tests/GhostWin.MeasurementDriver/Verification/PaneCountVerifier.cs`
- `tests/GhostWin.MeasurementDriver/Scenario/IdleScenario.cs`
- `tests/GhostWin.MeasurementDriver/Scenario/ResizeFourPaneScenario.cs`
- `tests/GhostWin.MeasurementDriver/Scenario/LoadScenario.cs`
- `tests/GhostWin.E2E.Tests/MeasurementDriver/DriverOptionsParserTests.cs`
- `tests/GhostWin.E2E.Tests/MeasurementDriver/DriverResultContractTests.cs`
- `tests/GhostWin.E2E.Tests/MeasurementDriver/PaneCountVerifierTests.cs`

### Modify

- `GhostWin.sln`
- `tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj`
- `scripts/measure_render_baseline.ps1`
- `C:/Users/Solit/obsidian/note/Projects/GhostWin/Milestones/m15-render-baseline-comparison.md`

### Responsibilities

- `scripts/measure_render_baseline.ps1`
  Keeps launch/build/log/CSV ownership. It must not start containing UIA tree walking or key choreography.
- `GhostWin.MeasurementDriver`
  Owns GhostWin window discovery, focus, pane split setup, load typing, and JSON result output.
- `GhostWin.E2E.Tests/MeasurementDriver/*`
  Owns fast tests for parser, result contract, and baseline validity logic.

---

### Task 1: Scaffold The Measurement Driver Project

**Files:**
- Create: `tests/GhostWin.MeasurementDriver/GhostWin.MeasurementDriver.csproj`
- Create: `tests/GhostWin.MeasurementDriver/Program.cs`
- Create: `tests/GhostWin.MeasurementDriver/Contracts/DriverOptions.cs`
- Create: `tests/GhostWin.MeasurementDriver/Contracts/DriverResult.cs`
- Create: `tests/GhostWin.E2E.Tests/MeasurementDriver/DriverOptionsParserTests.cs`
- Create: `tests/GhostWin.E2E.Tests/MeasurementDriver/DriverResultContractTests.cs`
- Modify: `tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj`
- Modify: `GhostWin.sln`

- [ ] **Step 1: Create the empty driver project shell and solution entry**

```xml
<!-- tests/GhostWin.MeasurementDriver/GhostWin.MeasurementDriver.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FlaUI.Core" Version="5.0.0" />
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
  </ItemGroup>
</Project>
```

```csharp
// tests/GhostWin.MeasurementDriver/Program.cs
Console.Error.WriteLine("measurement driver not implemented");
return 2;
```

- [ ] **Step 2: Add a project reference from the test hub to the new console project**

```xml
<!-- tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj -->
<ItemGroup>
  <ProjectReference Include="..\GhostWin.MeasurementDriver\GhostWin.MeasurementDriver.csproj" />
  <ProjectReference Include="..\..\src\GhostWin.Core\GhostWin.Core.csproj" />
  <ProjectReference Include="..\..\src\GhostWin.Services\GhostWin.Services.csproj" />
</ItemGroup>
```

- [ ] **Step 3: Write the failing contract tests**

```csharp
// tests/GhostWin.E2E.Tests/MeasurementDriver/DriverOptionsParserTests.cs
using FluentAssertions;
using GhostWin.MeasurementDriver.Contracts;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class DriverOptionsParserTests
{
    [Fact]
    public void Parse_ResizeFourPane_ParsesPidAndOutputPath()
    {
        var args = new[]
        {
            "--scenario", "resize-4pane",
            "--pid", "4242",
            "--output-json", "C:\\temp\\m15-driver.json"
        };

        var options = DriverOptions.Parse(args);

        options.Scenario.Should().Be("resize-4pane");
        options.GhostWinPid.Should().Be(4242);
        options.OutputJsonPath.Should().Be("C:\\temp\\m15-driver.json");
    }
}
```

```csharp
// tests/GhostWin.E2E.Tests/MeasurementDriver/DriverResultContractTests.cs
using FluentAssertions;
using GhostWin.MeasurementDriver.Contracts;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class DriverResultContractTests
{
    [Fact]
    public void Success_ResizeFourPane_SetsValidityFields()
    {
        var result = DriverResult.Success(
            scenario: "resize",
            mode: "4pane",
            observedPanes: 4);

        result.Valid.Should().BeTrue();
        result.ObservedPanes.Should().Be(4);
        result.Reason.Should().BeNull();
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~MeasurementDriver"
```

Expected:

```text
FAIL
CS0246 or CS0103 because DriverOptions / DriverResult do not exist yet
```

- [ ] **Step 5: Implement the minimal contract types and parser**

```csharp
// tests/GhostWin.MeasurementDriver/Contracts/DriverOptions.cs
namespace GhostWin.MeasurementDriver.Contracts;

public sealed record DriverOptions(
    string Scenario,
    int GhostWinPid,
    string OutputJsonPath,
    string? Workload = null)
{
    public static DriverOptions Parse(string[] args)
    {
        string? scenario = null;
        string? outputJson = null;
        int? pid = null;
        string? workload = null;

        for (var i = 0; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--scenario":
                    scenario = args[i + 1];
                    break;
                case "--pid":
                    pid = int.Parse(args[i + 1]);
                    break;
                case "--output-json":
                    outputJson = args[i + 1];
                    break;
                case "--workload":
                    workload = args[i + 1];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(scenario) || pid is null || string.IsNullOrWhiteSpace(outputJson))
        {
            throw new ArgumentException("Missing required measurement driver arguments.");
        }

        return new DriverOptions(scenario, pid.Value, outputJson, workload);
    }
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Contracts/DriverResult.cs
namespace GhostWin.MeasurementDriver.Contracts;

public sealed record DriverResult(
    string Scenario,
    string Mode,
    bool Valid,
    int? ObservedPanes,
    string? Reason)
{
    public static DriverResult Success(string scenario, string mode, int? observedPanes = null)
        => new(scenario, mode, true, observedPanes, null);

    public static DriverResult Failure(string scenario, string mode, string reason, int? observedPanes = null)
        => new(scenario, mode, false, observedPanes, reason);
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Program.cs
using System.Text.Json;
using GhostWin.MeasurementDriver.Contracts;

var options = DriverOptions.Parse(args);
var result = DriverResult.Success(options.Scenario, "pending");

Directory.CreateDirectory(Path.GetDirectoryName(options.OutputJsonPath)!);
await File.WriteAllTextAsync(
    options.OutputJsonPath,
    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

return 0;
```

- [ ] **Step 6: Run tests to verify they pass**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~MeasurementDriver"
```

Expected:

```text
PASS
2 passed, 0 failed
```

- [ ] **Step 7: Commit**

```powershell
git add tests/GhostWin.MeasurementDriver tests/GhostWin.E2E.Tests/MeasurementDriver tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj GhostWin.sln
git commit -m "feat: add m15 driver scaffold"
```

---

### Task 2: Add Validity Logic And Window Control Infrastructure

**Files:**
- Create: `tests/GhostWin.MeasurementDriver/Verification/PaneCountVerifier.cs`
- Create: `tests/GhostWin.MeasurementDriver/Infrastructure/Win32.cs`
- Create: `tests/GhostWin.MeasurementDriver/Infrastructure/MainWindowFinder.cs`
- Create: `tests/GhostWin.MeasurementDriver/Infrastructure/GhostWinController.cs`
- Create: `tests/GhostWin.E2E.Tests/MeasurementDriver/PaneCountVerifierTests.cs`
- Modify: `tests/GhostWin.MeasurementDriver/Program.cs`

- [ ] **Step 1: Write the failing pane validity tests**

```csharp
// tests/GhostWin.E2E.Tests/MeasurementDriver/PaneCountVerifierTests.cs
using FluentAssertions;
using GhostWin.MeasurementDriver.Verification;

namespace GhostWin.E2E.Tests.MeasurementDriver;

public class PaneCountVerifierTests
{
    [Fact]
    public void Evaluate_ReturnsFailure_WhenObservedPaneCountDiffers()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 2);

        result.Valid.Should().BeFalse();
        result.Reason.Should().Be("pane count mismatch (expected 4, observed 2)");
    }

    [Fact]
    public void Evaluate_ReturnsSuccess_WhenObservedPaneCountMatches()
    {
        var result = PaneCountVerifier.Evaluate(expected: 4, observed: 4);

        result.Valid.Should().BeTrue();
        result.Reason.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~PaneCountVerifierTests"
```

Expected:

```text
FAIL
CS0246 because PaneCountVerifier does not exist yet
```

- [ ] **Step 3: Implement the validity helper and minimal window infrastructure**

```csharp
// tests/GhostWin.MeasurementDriver/Verification/PaneCountVerifier.cs
using GhostWin.MeasurementDriver.Contracts;

namespace GhostWin.MeasurementDriver.Verification;

public static class PaneCountVerifier
{
    public static DriverResult Evaluate(int expected, int observed)
    {
        return observed == expected
            ? DriverResult.Success("resize", "4pane", observed)
            : DriverResult.Failure(
                "resize",
                "4pane",
                $"pane count mismatch (expected {expected}, observed {observed})",
                observed);
    }
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Infrastructure/Win32.cs
using System.Runtime.InteropServices;

namespace GhostWin.MeasurementDriver.Infrastructure;

internal static partial class Win32
{
    [LibraryImport("user32.dll")]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    internal const int SW_RESTORE = 9;
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Infrastructure/MainWindowFinder.cs
using System.Diagnostics;

namespace GhostWin.MeasurementDriver.Infrastructure;

internal static class MainWindowFinder
{
    public static nint WaitForMainWindow(int pid, TimeSpan timeout)
    {
        var start = Stopwatch.StartNew();
        while (start.Elapsed < timeout)
        {
            var process = Process.GetProcessById(pid);
            if (process.MainWindowHandle != nint.Zero)
            {
                return process.MainWindowHandle;
            }

            Thread.Sleep(100);
        }

        return nint.Zero;
    }
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Infrastructure/GhostWinController.cs
namespace GhostWin.MeasurementDriver.Infrastructure;

internal sealed class GhostWinController
{
    public nint MainWindowHandle { get; }

    public GhostWinController(nint mainWindowHandle)
    {
        MainWindowHandle = mainWindowHandle;
    }

    public void BringToForeground()
    {
        Win32.ShowWindow(MainWindowHandle, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(MainWindowHandle);
    }
}
```

- [ ] **Step 4: Update `Program.cs` to use the window finder**

```csharp
// tests/GhostWin.MeasurementDriver/Program.cs
using System.Text.Json;
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;

var options = DriverOptions.Parse(args);
var hwnd = MainWindowFinder.WaitForMainWindow(options.GhostWinPid, TimeSpan.FromSeconds(10));
if (hwnd == nint.Zero)
{
    var fail = DriverResult.Failure(options.Scenario, "driver", "main window not found");
    await File.WriteAllTextAsync(options.OutputJsonPath, JsonSerializer.Serialize(fail));
    return 1;
}

var controller = new GhostWinController(hwnd);
controller.BringToForeground();
var result = DriverResult.Success(options.Scenario, "driver");
await File.WriteAllTextAsync(options.OutputJsonPath, JsonSerializer.Serialize(result));
return 0;
```

- [ ] **Step 5: Run tests to verify they pass**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~PaneCountVerifierTests"
```

Expected:

```text
PASS
2 passed, 0 failed
```

- [ ] **Step 6: Commit**

```powershell
git add tests/GhostWin.MeasurementDriver tests/GhostWin.E2E.Tests/MeasurementDriver
git commit -m "feat: add m15 validity helpers"
```

---

### Task 3: Add Idle CPU Capture And Driver Invocation To The Script

**Files:**
- Modify: `scripts/measure_render_baseline.ps1`

- [ ] **Step 1: Add script helpers for driver path resolution and CPU capture**

```powershell
# scripts/measure_render_baseline.ps1
function Resolve-MeasurementDriverExe {
    param([string]$RepoRoot, [string]$Configuration)

    $driverRoot = Join-Path $RepoRoot "tests\GhostWin.MeasurementDriver\bin"
    $patterns = @(
        "x64\$Configuration\net10.0-windows\GhostWin.MeasurementDriver.exe",
        "$Configuration\net10.0-windows\GhostWin.MeasurementDriver.exe"
    )

    foreach ($pattern in $patterns) {
        $hit = Get-ChildItem -Path $driverRoot -Filter 'GhostWin.MeasurementDriver.exe' -Recurse -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like (Join-Path $driverRoot $pattern) } |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    throw "GhostWin.MeasurementDriver.exe not found under $driverRoot"
}

function Start-CpuCapture {
    param([int]$ProcessId, [string]$OutputCsv)

    $counters = @(
        '\Processor Information(_Total)\% Processor Utility',
        '\Process(GhostWin.App)\% Processor Time'
    )

    return Start-Process -FilePath 'typeperf.exe' `
        -ArgumentList @($counters + @('-si', '1', '-sc', '60', '-o', $OutputCsv, '-f', 'CSV')) `
        -PassThru -WindowStyle Hidden
}
```

- [ ] **Step 2: Add a JSON contract file path and invoke the driver before the capture window**

```powershell
# scripts/measure_render_baseline.ps1
$driverJson = Join-Path $OutputDir 'driver-result.json'
$cpuCsv     = Join-Path $OutputDir 'cpu.csv'
$driverExe  = Resolve-MeasurementDriverExe -RepoRoot $repoRoot -Configuration $Configuration

function Invoke-MeasurementDriver {
    param(
        [string]$DriverExe,
        [string]$Scenario,
        [int]$ProcessId,
        [string]$OutputJson,
        [string]$Workload
    )

    $args = @('--scenario', $Scenario, '--pid', "$ProcessId", '--output-json', $OutputJson)
    if ($Workload) {
        $args += @('--workload', $Workload)
    }

    $proc = Start-Process -FilePath $DriverExe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        throw "Measurement driver failed (exit $($proc.ExitCode))"
    }

    return Get-Content -LiteralPath $OutputJson -Raw | ConvertFrom-Json
}
```

- [ ] **Step 3: Add a short integration run for `idle` and verify it fails before CPU output exists**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 5 -Configuration Release
```

Expected:

```text
FAIL or missing cpu.csv because CPU capture and driver contract are not fully wired yet
```

- [ ] **Step 4: Wire CPU lifecycle into the existing app launch/finally block**

```powershell
# scripts/measure_render_baseline.ps1
$cpuProc = $null

$app = Start-Process -FilePath $appExe -PassThru
$driverResult = Invoke-MeasurementDriver -DriverExe $driverExe -Scenario $Scenario -ProcessId $app.Id -OutputJson $driverJson -Workload ''
$cpuProc = Start-CpuCapture -ProcessId $app.Id -OutputCsv $cpuCsv

try {
    Start-Sleep -Seconds $DurationSec
}
finally {
    if ($cpuProc -and -not $cpuProc.HasExited) {
        Stop-Process -Id $cpuProc.Id -Force -ErrorAction SilentlyContinue
    }
}
```

- [ ] **Step 5: Extend `summary.txt` with CPU artifact metadata**

```powershell
# scripts/measure_render_baseline.ps1
$lines += "cpu_csv:        $(Split-Path -Leaf $cpuCsv)"
$lines += "driver_valid:   $($driverResult.valid)"
if ($driverResult.observedPanes -ne $null) {
    $lines += "observed_panes: $($driverResult.observedPanes)"
}
if ($driverResult.reason) {
    $lines += "reason:         $($driverResult.reason)"
}
```

- [ ] **Step 6: Run idle verification to confirm the new artifacts exist**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 5 -Configuration Release
```

Expected:

```text
[baseline] output -> ...
[baseline] launched pid=...
[baseline] parsed ...
[baseline] summary -> ...\summary.txt
```

And verify:

```powershell
Get-ChildItem docs\04-report\features\m14-baseline\idle-* | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

Expected:

```text
ghostwin.log
render-perf.csv
summary.txt
cpu.csv
driver-result.json
```

- [ ] **Step 7: Commit**

```powershell
git add scripts/measure_render_baseline.ps1
git commit -m "feat: add idle cpu capture"
```

---

### Task 4: Implement The 4-Pane Resize Scenario And Validity Gate

**Files:**
- Create: `tests/GhostWin.MeasurementDriver/Scenario/ResizeFourPaneScenario.cs`
- Modify: `tests/GhostWin.MeasurementDriver/Program.cs`
- Modify: `tests/GhostWin.MeasurementDriver/Infrastructure/GhostWinController.cs`
- Modify: `scripts/measure_render_baseline.ps1`

- [ ] **Step 1: Write the failing test for resize mode result shaping**

```csharp
// tests/GhostWin.E2E.Tests/MeasurementDriver/DriverResultContractTests.cs
[Fact]
public void Failure_ResizeFourPane_UsesExpectedMode()
{
    var result = DriverResult.Failure(
        scenario: "resize",
        mode: "4pane",
        reason: "pane count mismatch (expected 4, observed 2)",
        observedPanes: 2);

    result.Mode.Should().Be("4pane");
    result.Valid.Should().BeFalse();
    result.ObservedPanes.Should().Be(2);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~DriverResultContractTests"
```

Expected:

```text
FAIL if the mode/invalid path is not yet shaped correctly
```

- [ ] **Step 3: Add minimal pane split automation to the driver**

```csharp
// tests/GhostWin.MeasurementDriver/Infrastructure/GhostWinController.cs
using FlaUI.Core.Input;

internal sealed class GhostWinController
{
    public nint MainWindowHandle { get; }

    public GhostWinController(nint mainWindowHandle)
    {
        MainWindowHandle = mainWindowHandle;
    }

    public void BringToForeground()
    {
        Win32.ShowWindow(MainWindowHandle, Win32.SW_RESTORE);
        Win32.SetForegroundWindow(MainWindowHandle);
        Thread.Sleep(250);
    }

    public void SplitVertical() => Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.MENU, FlaUI.Core.WindowsAPI.VirtualKeyShort.VK_V);
    public void SplitHorizontal() => Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.MENU, FlaUI.Core.WindowsAPI.VirtualKeyShort.VK_H);
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Scenario/ResizeFourPaneScenario.cs
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;
using GhostWin.MeasurementDriver.Verification;

namespace GhostWin.MeasurementDriver.Scenario;

internal static class ResizeFourPaneScenario
{
    public static DriverResult Execute(GhostWinController controller)
    {
        controller.BringToForeground();
        controller.SplitVertical();
        Thread.Sleep(300);
        controller.SplitHorizontal();
        Thread.Sleep(300);
        controller.SplitHorizontal();
        Thread.Sleep(500);

        var observedPanes = 4; // replace with UIA count in the next step
        return PaneCountVerifier.Evaluate(4, observedPanes);
    }
}
```

- [ ] **Step 4: Replace the placeholder pane count with UIA-based counting**

```csharp
// tests/GhostWin.MeasurementDriver/Scenario/ResizeFourPaneScenario.cs
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;

internal static int CountTerminalHosts(nint hwnd)
{
    using var automation = new UIA3Automation();
    var main = automation.FromHandle(hwnd).AsWindow();
    var hosts = main.FindAllDescendants(cf => cf.ByAutomationId("TerminalHost"));
    return hosts.Length;
}
```

```csharp
// tests/GhostWin.MeasurementDriver/Scenario/ResizeFourPaneScenario.cs
var observedPanes = CountTerminalHosts(controller.MainWindowHandle);
return PaneCountVerifier.Evaluate(4, observedPanes);
```

- [ ] **Step 5: Update the script to allow `-Scenario resize -Panes 4`**

```powershell
# scripts/measure_render_baseline.ps1
if ($Panes -gt 1 -and $Scenario -ne 'resize') {
    throw "Multi-pane mode is only valid for resize baseline."
}

if ($Scenario -eq 'resize' -and $Panes -eq 4) {
    $driverResult = Invoke-MeasurementDriver -DriverExe $driverExe -Scenario 'resize-4pane' -ProcessId $app.Id -OutputJson $driverJson -Workload ''
    if (-not $driverResult.valid) {
        Set-Content -LiteralPath $summaryFile -Value @(
            'scenario:       resize'
            'mode:           4-pane'
            'valid:          no'
            "reason:         $($driverResult.reason)"
            "observed_panes: $($driverResult.observedPanes)"
        )
        throw "4-pane resize baseline invalid: $($driverResult.reason)"
    }
}
```

- [ ] **Step 6: Run the resize-4pane verification**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario resize -Panes 4 -DurationSec 10 -Configuration Release
```

Expected:

```text
[baseline] resize automation finished
[baseline] parsed ...
[baseline] summary -> ...\summary.txt
```

And verify:

```powershell
Get-Content (Get-ChildItem docs\04-report\features\m14-baseline\resize-4pane-* | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'summary.txt' })
```

Expected:

```text
valid:          yes
observed_panes: 4
```

- [ ] **Step 7: Commit**

```powershell
git add tests/GhostWin.MeasurementDriver scripts/measure_render_baseline.ps1 tests/GhostWin.E2E.Tests/MeasurementDriver
git commit -m "feat: automate 4pane resize baseline"
```

---

### Task 5: Implement The Fixed Load Scenario

**Files:**
- Create: `tests/GhostWin.MeasurementDriver/Scenario/LoadScenario.cs`
- Modify: `tests/GhostWin.MeasurementDriver/Program.cs`
- Modify: `scripts/measure_render_baseline.ps1`

- [ ] **Step 1: Write the failing test for default workload selection**

```csharp
// tests/GhostWin.E2E.Tests/MeasurementDriver/DriverOptionsParserTests.cs
[Fact]
public void Parse_LoadWithoutWorkload_UsesDefaultSystem32Workload()
{
    var args = new[]
    {
        "--scenario", "load",
        "--pid", "5151",
        "--output-json", "C:\\temp\\driver.json"
    };

    var options = DriverOptions.Parse(args);

    options.Workload.Should().Be("Get-ChildItem -Recurse C:\\Windows\\System32 | Format-List");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~DriverOptionsParserTests"
```

Expected:

```text
FAIL because Workload is null or not defaulted yet
```

- [ ] **Step 3: Default the workload in `DriverOptions.Parse`**

```csharp
// tests/GhostWin.MeasurementDriver/Contracts/DriverOptions.cs
public static DriverOptions Parse(string[] args)
{
    // existing parse...
    if (string.Equals(scenario, "load", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(workload))
    {
        workload = @"Get-ChildItem -Recurse C:\Windows\System32 | Format-List";
    }

    return new DriverOptions(scenario, pid.Value, outputJson, workload);
}
```

- [ ] **Step 4: Implement the load scenario using the real keyboard input path**

```csharp
// tests/GhostWin.MeasurementDriver/Scenario/LoadScenario.cs
using GhostWin.MeasurementDriver.Contracts;
using GhostWin.MeasurementDriver.Infrastructure;
using FlaUI.Core.Input;

namespace GhostWin.MeasurementDriver.Scenario;

internal static class LoadScenario
{
    public static DriverResult Execute(GhostWinController controller, string workload)
    {
        controller.BringToForeground();
        Keyboard.Type(workload);
        Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        Keyboard.Release(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        Thread.Sleep(500);
        return DriverResult.Success("load", "1pane");
    }
}
```

- [ ] **Step 5: Dispatch `load` in `Program.cs` and call the driver from the script**

```csharp
// tests/GhostWin.MeasurementDriver/Program.cs
using GhostWin.MeasurementDriver.Scenario;

DriverResult result = options.Scenario switch
{
    "idle" => DriverResult.Success("idle", "1pane"),
    "resize-4pane" => ResizeFourPaneScenario.Execute(controller),
    "load" => LoadScenario.Execute(controller, options.Workload!),
    _ => DriverResult.Failure(options.Scenario, "driver", "unsupported scenario")
};
```

```powershell
# scripts/measure_render_baseline.ps1
if ($Scenario -eq 'load') {
    $driverResult = Invoke-MeasurementDriver -DriverExe $driverExe -Scenario 'load' -ProcessId $app.Id -OutputJson $driverJson -Workload ''
    if (-not $driverResult.valid) {
        throw "Load baseline invalid: $($driverResult.reason)"
    }
}
```

- [ ] **Step 6: Run load verification**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario load -DurationSec 10 -Configuration Release
```

Expected:

```text
[baseline] load
[baseline] parsed ...
[baseline] summary -> ...\summary.txt
```

And verify:

```powershell
Get-ChildItem docs\04-report\features\m14-baseline\load-* | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

Expected:

```text
ghostwin.log
render-perf.csv
summary.txt
cpu.csv
driver-result.json
```

- [ ] **Step 7: Commit**

```powershell
git add tests/GhostWin.MeasurementDriver scripts/measure_render_baseline.ps1 tests/GhostWin.E2E.Tests/MeasurementDriver
git commit -m "feat: automate load baseline"
```

---

### Task 6: Stage A Verification And Doc Sync

**Files:**
- Modify: `scripts/measure_render_baseline.ps1`
- Modify: `C:/Users/Solit/obsidian/note/Projects/GhostWin/Milestones/m15-render-baseline-comparison.md`

- [ ] **Step 1: Normalize artifact folder names from `m14-baseline` to `m15-baseline`**

```powershell
# scripts/measure_render_baseline.ps1
$OutputDir = Join-Path $repoRoot "docs\04-report\features\m15-baseline\$tag"
```

- [ ] **Step 2: Run the three Stage A scenarios end-to-end**

Run:

```powershell
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario idle -DurationSec 10 -Configuration Release
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario resize -Panes 4 -DurationSec 10 -Configuration Release
powershell -File .\scripts\measure_render_baseline.ps1 -Scenario load -DurationSec 10 -Configuration Release
```

Expected:

```text
All three runs finish with render-perf.csv + summary.txt + cpu.csv artifacts
```

- [ ] **Step 3: Build the solution in Debug and Release**

Run:

```powershell
msbuild GhostWin.sln /p:Configuration=Debug /p:Platform=x64 /nologo /verbosity:minimal
msbuild GhostWin.sln /p:Configuration=Release /p:Platform=x64 /nologo /verbosity:minimal
```

Expected:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

- [ ] **Step 4: Run the focused tests**

Run:

```powershell
dotnet test tests/GhostWin.E2E.Tests/GhostWin.E2E.Tests.csproj --filter "FullyQualifiedName~MeasurementDriver"
```

Expected:

```text
PASS
All MeasurementDriver tests green
```

- [ ] **Step 5: Update the M-15 Obsidian milestone stub to reflect Stage A completion**

```markdown
## 진행 상황

- Stage A 내부 기준선 자동화 완료
- idle: `render-perf.csv` + `cpu.csv` 확보
- resize-4pane: pane 수 검증 후 CSV 확보
- load: fixed workload 자동화 확보
- 다음 단계: WT / WezTerm / Alacritty 비교 (Stage B)
```

- [ ] **Step 6: Commit**

```powershell
git add scripts/measure_render_baseline.ps1 docs/04-report/features/m15-baseline C:/Users/Solit/obsidian/note/Projects/GhostWin/Milestones/m15-render-baseline-comparison.md
git commit -m "docs: record m15 stagea baseline"
```

---

## Self-Review

- Spec coverage:
  - Stage A only by design. G1/G4/G5 are covered directly in Tasks 3-6.
  - G2 external comparison is intentionally deferred to a separate Stage B plan after Stage A artifacts stabilize.
- Placeholder scan:
  - No `TODO`, `TBD`, or "implement later" placeholders remain.
  - The only deferred work is explicitly named as Stage B and excluded from this plan's scope.
- Type consistency:
  - `DriverOptions`, `DriverResult`, `PaneCountVerifier`, and `GhostWinController` names are consistent across tasks.
  - The script-driver JSON handoff always uses `driver-result.json`.

---

## Execution Handoff

Plan complete and saved to `docs/01-plan/features/m15-render-baseline-comparison.plan.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
