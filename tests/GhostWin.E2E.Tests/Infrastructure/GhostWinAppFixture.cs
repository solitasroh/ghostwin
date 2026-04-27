using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FlaUIApp = FlaUI.Core.Application;

namespace GhostWin.E2E.Tests.Infrastructure;

/// <summary>
/// xUnit IClassFixture — 테스트 클래스당 앱을 1회 Launch/Close 하는 생명주기 관리자.
///
/// tests/e2e-flaui-split-content/UiaProbe.cs 의 6단계 로직을 xUnit ClassFixture 로 재포장:
///   Step 1: Application.Launch(exePath)
///   Step 2: GetMainWindow(automation, timeout)
///   Step 3: Properties.Name / Properties.AutomationId 읽기 가능
///   Step 4: FindAllChildren() 가능
///   Step 5: FindAllDescendants() 가능
///   Step 6: MainWindow 핸들 유효
///
/// 사용 방법:
///   public class MyTests : IClassFixture&lt;GhostWinAppFixture&gt;
///   {
///       public MyTests(GhostWinAppFixture fixture) { _fixture = fixture; }
///   }
///
/// 주의:
///   - Tier 2/3 시나리오 전용. Tier 1 (FileStateScenarios) 은 별도 ProcessStartInfo 사용.
///   - 같은 [Collection] 내에서 앱 인스턴스를 공유. 상태 격리가 필요한 경우 ClassFixture 대신
///     IDisposable 내부에서 앱을 재시작하는 별도 픽스처 설계 필요.
/// </summary>
public sealed class GhostWinAppFixture : IDisposable
{
    /// <summary>FlaUI Application 핸들 (Process 포함)</summary>
    public FlaUIApp App { get; }

    /// <summary>UIA3 자동화 객체 — FindDescendants 등 UIA 쿼리에 사용</summary>
    public UIA3Automation Automation { get; }

    /// <summary>메인 윈도우 AutomationElement — InvokePattern, FindDescendant 등에 사용</summary>
    public AutomationElement MainWindow { get; }

    public GhostWinAppFixture()
    {
        // 이전 인스턴스 정리 (테스트 실패 잔재 방지)
        AppRunner.StopRunning();

        var exePath = AppRunner.FindAppExe();

        // FlaUI.Core.Application.Launch(string) 는 내부적으로 WorkingDirectory="" 로
        // CreateProcess 를 호출해, test runner 의 CWD 를 그대로 상속한다.
        // Tier 1 테스트(UseShellExecute=false + 명시적 WorkingDirectory)가 먼저 실행되면
        // test 프로세스의 CWD 가 C:\temp 등 접근 불가 경로로 오염될 수 있어
        // Win32 ERROR_ACCESS_DENIED 가 발생한다.
        // ProcessStartInfo 오버로드에서 WorkingDirectory 를 exe 디렉토리로 고정해 해결.
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        App = FlaUIApp.Launch(psi);
        Automation = new UIA3Automation();

        // UiaProbe.cs Step 2 패턴: PowerShell 프롬프트 렌더 대기 후 GetMainWindow
        Thread.Sleep(AppRunner.DefaultWaitForPeb);

        MainWindow = App.GetMainWindow(Automation, TimeSpan.FromSeconds(8))
                    ?? throw new InvalidOperationException(
                           $"GhostWin main window 를 찾을 수 없습니다. " +
                           $"exe: {exePath}, PID: {App.ProcessId}");
    }

    public void Dispose()
    {
        try
        {
            Automation.Dispose();
        }
        catch
        {
            // best effort
        }

        try
        {
            App.Close();
            // 최대 2초 대기 후 강제 종료
            if (!App.WaitWhileMainHandleIsMissing(TimeSpan.FromSeconds(2)))
            {
                var proc = Process.GetProcessById(App.ProcessId);
                proc.Kill();
                proc.WaitForExit(1000);
            }
        }
        catch
        {
            // best effort — 이미 종료됐거나 권한 없음
        }
    }
}
