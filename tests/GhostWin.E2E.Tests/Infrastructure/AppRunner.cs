using System.Diagnostics;
using System.Reflection;

namespace GhostWin.E2E.Tests.Infrastructure;

/// <summary>
/// GhostWin.App.exe 프로세스 시작/종료 헬퍼.
/// scripts/test_m11_*.ps1 의 Stop-RunningApp / Start-Process -WorkingDirectory /
/// $proc.CloseMainWindow() + WaitForExit(8000) + Kill fallback 패턴을 1:1 이식.
/// </summary>
public static class AppRunner
{
    // 환경변수 오버라이드 키
    private const string AppExeEnvVar  = "GHOSTWIN_APP_EXE";
    private const string WaitSecsEnvVar = "GHOSTWIN_E2E_WAIT_SECONDS";

    // 기본 PEB 폴링 대기 시간 (PS1 원본과 동일: 4초)
    public static TimeSpan DefaultWaitForPeb =>
        TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(WaitSecsEnvVar), out var v) ? v : 4);

    /// <summary>
    /// 레포 루트 경로.
    /// Assembly 경로에서 GhostWin.sln 이 있는 디렉토리까지 상위 탐색.
    /// </summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                  ?? AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GhostWin.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "GhostWin.sln 을 찾을 수 없습니다. RepoRoot 탐색 실패. " +
            $"시작 경로: {dir}");
    }

    /// <summary>
    /// GhostWin.App.exe 경로를 반환한다.
    /// 우선 순위:
    ///   1. 환경변수 GHOSTWIN_APP_EXE
    ///   2. bin\x64\Debug\net10.0-windows  (VS 빌드 기본)
    ///   3. bin\x64\Release\net10.0-windows
    ///   4. bin\Debug\net10.0-windows\win-x64 (PS1 스크립트와 동일)
    ///   5. bin\Debug\net10.0-windows
    /// </summary>
    public static string FindAppExe()
    {
        var envPath = Environment.GetEnvironmentVariable(AppExeEnvVar);
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        // 우선순위: scripts/test_m11_*.ps1 이 사용하는 경로 → VS Platform=x64 → Release
        // 중요: bin\x64\Debug 는 이전 빌드일 수 있어 크래시 발생 가능 → 가장 낮은 우선순위
        var candidates = new[]
        {
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows10.0.22621.0\win-x64\GhostWin.App.exe"),
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows\win-x64\GhostWin.App.exe"),
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\Debug\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\x64\Release\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\Release\net10.0-windows\GhostWin.App.exe"),
            Path.Combine(RepoRoot, @"src\GhostWin.App\bin\x64\Debug\net10.0-windows\GhostWin.App.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        throw new FileNotFoundException(
            $"GhostWin.App.exe 를 찾을 수 없습니다.\n" +
            $"환경변수 {AppExeEnvVar} 로 경로를 명시하거나, 먼저 GhostWin.sln 을 빌드하세요.\n" +
            $"탐색한 경로:\n  " + string.Join("\n  ", candidates));
    }

    /// <summary>
    /// 기본 CWD (RepoRoot) 로 GhostWin.App.exe 를 시작한다.
    /// </summary>
    public static Process StartDefault() => StartWithCwd(RepoRoot);

    /// <summary>
    /// 지정한 CWD 로 GhostWin.App.exe 를 시작한다.
    /// PS1: Start-Process -FilePath $appExe -WorkingDirectory $cwd -PassThru
    /// </summary>
    public static Process StartWithCwd(string cwd)
    {
        var exePath = FindAppExe();
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = cwd,
            UseShellExecute  = false,
        };
        return Process.Start(psi)
               ?? throw new InvalidOperationException(
                   $"Process.Start({exePath}) 이 null 을 반환했습니다.");
    }

    /// <summary>
    /// 프로세스를 정상 종료한다. 타임아웃 초과 시 강제 Kill.
    /// PS1: $proc.CloseMainWindow() + WaitForExit(8000) + $proc.Kill()
    /// </summary>
    public static void CloseGracefully(Process proc, TimeSpan? timeout = null)
    {
        var t = timeout ?? TimeSpan.FromSeconds(8);
        if (proc.HasExited) return;

        proc.CloseMainWindow();
        if (!proc.WaitForExit((int)t.TotalMilliseconds))
            proc.Kill();
    }

    /// <summary>
    /// 실행 중인 GhostWin.App.exe 인스턴스를 모두 강제 종료한다.
    /// PS1: Stop-RunningApp
    /// </summary>
    public static void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName("GhostWin.App"))
        {
            try
            {
                p.Kill();
                p.WaitForExit(2000);
            }
            catch
            {
                // best effort — 이미 종료됐거나 권한 없음
            }
        }
    }
}
