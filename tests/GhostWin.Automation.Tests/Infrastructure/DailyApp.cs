using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using FluentAssertions;
using GhostWin.Automation.Core;
using GhostWin.Core.Models;
using FlaUIApplication = FlaUI.Core.Application;

namespace GhostWin.Automation.Tests.Infrastructure;

internal sealed class DailyApp : IDisposable
{
    private readonly UIA3Automation _automation;
    private FlaUIApplication _app;
    private bool _disposed;

    private DailyApp(
        string repoRoot,
        AppSession session,
        AppSession launchedSession,
        TestControlClient client,
        UIA3Automation automation,
        FlaUIApplication app,
        Window window)
    {
        RepoRoot = repoRoot;
        Session = session;
        LaunchedSession = launchedSession;
        Client = client;
        _automation = automation;
        _app = app;
        Window = window;
        Artifacts = new ArtifactWriter(session.ArtifactDir);
    }

    public string RepoRoot { get; }

    public AppSession Session { get; private set; }

    public AppSession LaunchedSession { get; private set; }

    public TestControlClient Client { get; private set; }

    public Window Window { get; private set; }

    public ArtifactWriter Artifacts { get; }

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("GHOSTWIN_AUTOMATION_RUN_REAL_APP") == "1";

    public static async Task<DailyApp?> LaunchAsync(string testName)
    {
        if (!IsEnabled)
            return null;

        var repoRoot = FindRepoRoot();
        var root = Path.Combine(Path.GetTempPath(), "ghostwin-automation-daily", Guid.NewGuid().ToString("N"));
        var session = AppSession.Create(ToRunId(testName), root);
        var launched = new AppLauncher(repoRoot).Launch(session, TimeSpan.FromSeconds(10));
        var client = CreateClient(launched);
        var artifacts = new ArtifactWriter(session.ArtifactDir);
        UIA3Automation? automation = null;
        FlaUIApplication? app = null;

        try
        {
            await WaitForReadyAsync(client, artifacts);

            automation = new UIA3Automation();
            app = FlaUIApplication.Attach(launched.Pid!.Value);
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(5));
            window.Should().NotBeNull();

            return new DailyApp(repoRoot, session, launched, client, automation, app, window!);
        }
        catch
        {
            app?.Dispose();
            automation?.Dispose();
            if (launched.Pid is { } pid)
                new AppProcessTerminator().TerminateByPid(pid, TimeSpan.FromSeconds(10));
            throw;
        }
    }

    public async Task<TestControlState> WaitForReadyAsync()
        => await WaitForReadyAsync(Client, Artifacts);

    public async Task<TestControlState> WaitForStateAsync(
        string reason,
        Func<TestControlState, bool> predicate,
        TimeSpan? timeout = null)
    {
        TestControlState? state = null;
        var ready = await new Waiter().WaitUntilAsync(
            reason,
            async () =>
            {
                state = await Client.GetStateAsync();
                return predicate(state);
            },
            timeout ?? TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            Artifacts);

        if (!ready.Succeeded)
        {
            if (state != null)
                Artifacts.WriteAppState(state);
            Artifacts.WriteUiaTree(DumpKnownAutomationIds());
        }

        ready.Succeeded.Should().BeTrue(reason);
        return state!;
    }

    public async Task<AutomationElement> WaitForElementAsync(
        string automationId,
        TimeSpan? timeout = null)
    {
        AutomationElement? element = null;
        var ready = await new Waiter().WaitUntilAsync(
            $"uia element {automationId}",
            () =>
            {
                element = FindElementByAutomationId(automationId);
                return Task.FromResult(element != null);
            },
            timeout ?? TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            Artifacts);

        if (!ready.Succeeded)
        {
            Artifacts.WriteUiaTree(DumpKnownAutomationIds());
            var state = await Client.GetStateAsync();
            Artifacts.WriteAppState(state);
        }

        ready.Succeeded.Should().BeTrue($"AutomationId '{automationId}' should exist");
        return element!;
    }

    public string ReadElementText(string automationId)
    {
        var element = FindElementByAutomationId(automationId);
        element.Should().NotBeNull($"AutomationId '{automationId}' should exist");
        return ReadElementText(element!);
    }

    public async Task RelaunchAsync()
    {
        TerminateCurrentProcess();
        _app.Dispose();
        LaunchedSession = new AppLauncher(RepoRoot).Launch(Session, TimeSpan.FromSeconds(10));
        Client = CreateClient(LaunchedSession);
        await WaitForReadyAsync();
        _app = FlaUIApplication.Attach(LaunchedSession.Pid!.Value);
        var window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(5));
        window.Should().NotBeNull();
        Window = window!;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            var state = Client.GetStateAsync().GetAwaiter().GetResult();
            Artifacts.WriteAppState(state);
        }
        catch
        {
            // Best effort diagnostics only.
        }

        TerminateCurrentProcess();
        _app.Dispose();
        _automation.Dispose();
    }

    private static async Task<TestControlState> WaitForReadyAsync(
        TestControlClient client,
        ArtifactWriter artifacts)
    {
        TestControlState? state = null;
        var ready = await new Waiter().WaitUntilAsync(
            "daily app ready",
            async () =>
            {
                try
                {
                    state = await client.GetStateAsync();
                    return state.WorkspaceCount > 0 &&
                           state.SessionCount > 0 &&
                           state.PaneCount > 0 &&
                           state.ActiveWorkspaceId != null &&
                           state.ActiveSessionId != null;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (TimeoutException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(100),
            artifacts);

        ready.Succeeded.Should().BeTrue();
        return state!;
    }

    private void TerminateCurrentProcess()
    {
        if (LaunchedSession.Pid is { } pid)
        {
            new AppProcessTerminator().TerminateByPid(pid, TimeSpan.FromSeconds(10));
            LaunchedSession = LaunchedSession with { Pid = null, MainWindowHandle = IntPtr.Zero };
        }
    }

    private string DumpKnownAutomationIds()
    {
        var ids = new[]
        {
            AutomationIds.NewWorkspace,
            AutomationIds.SplitVertical,
            AutomationIds.SplitHorizontal,
            AutomationIds.ClosePane,
            AutomationIds.SettingsPage,
            AutomationIds.NotificationPanel,
            AutomationIds.CommandPalette,
            AutomationIds.MouseCursorShape,
            AutomationIds.MouseCursorVersion
        };

        return string.Join(
            Environment.NewLine,
            ids.Select(id =>
            {
                var element = FindElementByAutomationId(id);
                return $"{id}\t{(element == null ? "missing" : ReadElementText(element))}";
            }));
    }

    private AutomationElement? FindElementByAutomationId(string automationId)
    {
        var element = Window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element != null)
            return element;

        foreach (var topLevel in _app.GetAllTopLevelWindows(_automation))
        {
            if (topLevel.Properties.AutomationId.ValueOrDefault == automationId)
                return topLevel;

            element = topLevel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element != null)
                return element;
        }

        return null;
    }

    private static string ReadElementText(AutomationElement element)
    {
        var helpText = element.Properties.HelpText.ValueOrDefault;
        if (!string.IsNullOrEmpty(helpText))
            return helpText;

        var name = element.Properties.Name.ValueOrDefault;
        if (!string.IsNullOrEmpty(name))
            return name;

        var valuePattern = element.Patterns.Value.PatternOrDefault;
        if (valuePattern != null)
            return valuePattern.Value.Value ?? string.Empty;

        return string.Empty;
    }

    private static TestControlClient CreateClient(AppSession session)
        => new(
            pipeName: AppLauncher.GetHookPipeName(session),
            connectTimeout: TimeSpan.FromSeconds(1));

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

    private static string ToRunId(string testName)
        => string.Concat(testName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
}
