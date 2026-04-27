# Design — Phase 6-B: 알림 인프라 (Notification Infrastructure)

> **문서 종류**: Design (구현 명세)
> **작성일**: 2026-04-16
> **Plan 참조**: `docs/01-plan/features/phase-6-b-notification-infra.plan.md`
> **PRD 참조**: `docs/00-pm/phase-6-b-notification-infra.prd.md`

---

## 1. 한 줄 요약

Phase 6-A의 `IOscNotificationService` + `OscNotificationMessage`를 확장하여 **알림 패널(인메모리 100건 히스토리)** + **에이전트 상태 배지(5-state)** + **Toast 클릭→탭 전환** 3가지 기능을 WPF Grid Column + 기존 Messenger 패턴으로 구현.

---

## 2. 구현 순서 (4 Waves)

| Wave | 범위 | 의존 | 검증 |
|:----:|------|:---:|------|
| **W1** | 모델 + 서비스 확장 (NotificationEntry, AgentState, IOscNotificationService) | — | 빌드 성공 |
| **W2** | 알림 패널 WPF UI + 키보드 바인딩 (Ctrl+Shift+I/U) | W1 | 수동: 패널 토글, 항목 클릭→탭 전환 |
| **W3** | AgentState 상태 전환 + 사이드바 배지 | W1 | 수동: 출력 시 초록 원, OSC 시 파란 원 |
| **W4** | Toast 클릭 액션 + 통합 검증 | W1-W3 | 수동: Toast 클릭→창 복원+탭 전환 |

---

## 3. Wave 1 — 모델 + 서비스 확장

### 3.1 AgentState enum (신규)

**파일**: `src/GhostWin.Core/Models/AgentState.cs`

```csharp
namespace GhostWin.Core.Models;

public enum AgentState
{
    Idle,
    Running,
    WaitingForInput,
    Error,
    Completed
}
```

### 3.2 NotificationEntry record (신규)

**파일**: `src/GhostWin.Core/Models/NotificationEntry.cs`

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace GhostWin.Core.Models;

public partial class NotificationEntry : ObservableObject
{
    public uint SessionId { get; init; }
    public string SessionTitle { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }

    [ObservableProperty]
    private bool _isRead;
}
```

`ObservableObject` 상속 이유: `IsRead` 변경 시 UI 바인딩 갱신 필요.

### 3.3 SessionInfo 확장

**파일**: `src/GhostWin.Core/Models/SessionInfo.cs` — 기존 파일에 추가

```csharp
// 기존 필드 유지 + 추가
[ObservableProperty]
private AgentState _agentState = AgentState.Idle;

[ObservableProperty]
private DateTimeOffset _lastOutputTime = DateTimeOffset.MinValue;
```

### 3.4 WorkspaceInfo 확장

**파일**: `src/GhostWin.Core/Models/WorkspaceInfo.cs` — 기존 파일에 추가

```csharp
[ObservableProperty]
private AgentState _agentState = AgentState.Idle;
```

### 3.5 IOscNotificationService 확장

**파일**: `src/GhostWin.Core/Interfaces/IOscNotificationService.cs`

```csharp
using System.Collections.ObjectModel;
using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IOscNotificationService
{
    void HandleOscEvent(uint sessionId, string title, string body);
    void DismissAttention(uint sessionId);

    // Phase 6-B 추가
    ObservableCollection<NotificationEntry> Notifications { get; }
    int UnreadCount { get; }
    void MarkAsRead(NotificationEntry entry);
    void MarkAllAsRead();
    NotificationEntry? GetMostRecentUnread();
}
```

### 3.6 OscNotificationService 확장

**파일**: `src/GhostWin.Services/OscNotificationService.cs`

기존 코드를 확장. 핵심 변경점:

```csharp
public class OscNotificationService : ObservableObject, IOscNotificationService
{
    private const int MaxNotifications = 100;

    public ObservableCollection<NotificationEntry> Notifications { get; } = [];

    [ObservableProperty]
    private int _unreadCount;

    public void HandleOscEvent(uint sessionId, string title, string body)
    {
        if (!_settings.Current.Notifications.RingEnabled) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastNotifyTime < DebounceInterval) return;
        _lastNotifyTime = now;

        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;

        // 기존: NeedsAttention + Workspace 전파
        bool isActiveSession = session.IsActive;
        if (!isActiveSession)
        {
            session.NeedsAttention = true;
            session.AgentState = AgentState.WaitingForInput;
        }
        session.LastOscMessage = string.IsNullOrEmpty(body) ? title : body;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            if (!isActiveSession)
            {
                ws.NeedsAttention = true;
                ws.AgentState = AgentState.WaitingForInput;
            }
            ws.LastOscMessage = session.LastOscMessage;
        }

        // Phase 6-B: 알림 히스토리에 추가
        var entry = new NotificationEntry
        {
            SessionId = sessionId,
            SessionTitle = ws?.Title ?? session.Title,
            Title = title,
            Body = body,
            ReceivedAt = now,
            IsRead = isActiveSession  // 활성 세션이면 즉시 읽음 처리
        };
        Notifications.Insert(0, entry);
        if (Notifications.Count > MaxNotifications)
            Notifications.RemoveAt(Notifications.Count - 1);
        UpdateUnreadCount();

        _messenger.Send(new OscNotificationMessage(sessionId, title, body));
    }

    public void MarkAsRead(NotificationEntry entry)
    {
        entry.IsRead = true;
        UpdateUnreadCount();
    }

    public void MarkAllAsRead()
    {
        foreach (var n in Notifications)
            n.IsRead = true;
        UpdateUnreadCount();
    }

    public NotificationEntry? GetMostRecentUnread()
        => Notifications.FirstOrDefault(n => !n.IsRead);

    private void UpdateUnreadCount()
        => UnreadCount = Notifications.Count(n => !n.IsRead);

    // DismissAttention: 기존 코드 유지 + AgentState 리셋
    public void DismissAttention(uint sessionId)
    {
        var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session is null) return;
        session.NeedsAttention = false;
        session.LastOscMessage = string.Empty;
        if (session.AgentState == AgentState.WaitingForInput)
            session.AgentState = AgentState.Idle;

        var ws = _workspaceService.FindWorkspaceBySessionId(sessionId);
        if (ws is not null)
        {
            ws.NeedsAttention = false;
            ws.LastOscMessage = string.Empty;
            if (ws.AgentState == AgentState.WaitingForInput)
                ws.AgentState = AgentState.Idle;
        }
    }
}
```

**스레드 안전**: `HandleOscEvent`는 `Dispatcher.BeginInvoke` 경유로 UI 스레드에서만 호출됨 (Phase 6-A에서 검증됨, `MainWindow.xaml.cs:176-179`의 `OnOscNotify` 콜백).
`ObservableCollection`은 UI 스레드 전용이므로 별도 동기화 불필요.

### 3.7 NotificationSettings 확장

**파일**: `src/GhostWin.Core/Models/AppSettings.cs`

```csharp
public sealed class NotificationSettings
{
    public bool RingEnabled { get; set; } = true;
    public bool ToastEnabled { get; set; } = true;
    public bool PanelEnabled { get; set; } = true;   // Phase 6-B
    public bool BadgeEnabled { get; set; } = true;    // Phase 6-B
}
```

---

## 4. Wave 2 — 알림 패널 WPF UI

### 4.1 설계 결정: Grid Column 방식 (D-1 확정)

Popup/Flyout 대신 **MainWindow Grid에 Column을 추가**하여 패널 삽입.

근거:
- D3D11 SwapChainPanel과의 Airspace 충돌 완전 회피
- Phase 5 Pane 분할에서 Grid 기반 레이아웃이 검증됨
- 애니메이션은 `DoubleAnimation` + `Width` 바인딩으로 슬라이드 효과

### 4.2 MainWindow.xaml 변경

현재 레이아웃 (3 Column):
```
Column 0: Sidebar (사이드바)
Column 1: Divider (1px)
Column 2: Terminal (PaneContainer)
```

Phase 6-B 레이아웃 (5 Column):
```
Column 0: Sidebar (사이드바)
Column 1: Divider (1px)
Column 2: NotificationPanel (0 or 280px, 토글)
Column 3: Divider (1px, 패널 열림 시만 표시)
Column 4: Terminal (PaneContainer)
```

**Grid ColumnDefinition 변경**:

```xaml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="{Binding SidebarWidth}"/>
    <ColumnDefinition Width="1"/>
    <!-- Phase 6-B: 알림 패널 (닫힘=0, 열림=280) -->
    <ColumnDefinition Width="{Binding NotificationPanelWidth}"/>
    <ColumnDefinition Width="{Binding NotificationDividerWidth}"/>
    <ColumnDefinition Width="*"/>
</Grid.ColumnDefinitions>
```

기존 Divider와 PaneContainer의 `Grid.Column` 값을 각각 3, 4로 변경.

### 4.3 NotificationPanelControl (신규 UserControl)

**파일**: `src/GhostWin.App/Controls/NotificationPanelControl.xaml`

```xaml
<UserControl x:Class="GhostWin.App.Controls.NotificationPanelControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:models="clr-namespace:GhostWin.Core.Models;assembly=GhostWin.Core"
             Background="#1C1C1E">

    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>

    <DockPanel>
        <!-- 헤더 -->
        <Border DockPanel.Dock="Top" Padding="12,10"
                BorderBrush="#3A3A3C" BorderThickness="0,0,0,1">
            <DockPanel>
                <TextBlock Text="NOTIFICATIONS" FontSize="11" FontWeight="SemiBold"
                           Foreground="#8E8E93" VerticalAlignment="Center"
                           FontFamily="Segoe UI Variable"/>
                <Button DockPanel.Dock="Right" Content="Mark All Read"
                        FontSize="10" Foreground="#0091FF" Background="Transparent"
                        BorderThickness="0" HorizontalAlignment="Right"
                        Command="{Binding MarkAllReadCommand}"
                        AutomationProperties.AutomationId="E2E_MarkAllRead"/>
            </DockPanel>
        </Border>

        <!-- 미읽음 카운트 -->
        <Border DockPanel.Dock="Top" Padding="12,6"
                Visibility="{Binding HasUnread, Converter={StaticResource BoolToVis}}">
            <TextBlock FontSize="11" Foreground="#FFB020"
                       FontFamily="Segoe UI Variable">
                <Run Text="{Binding UnreadCount, Mode=OneWay}"/>
                <Run Text=" unread"/>
            </TextBlock>
        </Border>

        <!-- 알림 리스트 -->
        <ListBox ItemsSource="{Binding Notifications}"
                 Background="Transparent" BorderThickness="0"
                 ScrollViewer.HorizontalScrollBarVisibility="Disabled"
                 AutomationProperties.AutomationId="E2E_NotificationList">
            <ListBox.ItemTemplate>
                <DataTemplate DataType="{x:Type models:NotificationEntry}">
                    <Border Padding="12,8" Cursor="Hand"
                            Background="Transparent"
                            BorderBrush="#2C2C2E" BorderThickness="0,0,0,1">
                        <Border.InputBindings>
                            <MouseBinding MouseAction="LeftClick"
                                          Command="{Binding DataContext.NotificationClickCommand,
                                              RelativeSource={RelativeSource AncestorType=ListBox}}"
                                          CommandParameter="{Binding}"/>
                        </Border.InputBindings>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="16"/>
                                <ColumnDefinition Width="*"/>
                            </Grid.ColumnDefinitions>

                            <!-- 미읽음 표시자 -->
                            <Ellipse Grid.Column="0" Width="6" Height="6"
                                     Fill="#007AFF" VerticalAlignment="Top"
                                     Margin="0,6,0,0"
                                     Visibility="{Binding IsRead,
                                         Converter={StaticResource InverseBoolToVis}}"/>
                            <TextBlock Grid.Column="0" Text="✓" FontSize="9"
                                       Foreground="#636366" VerticalAlignment="Top"
                                       Margin="0,2,0,0"
                                       Visibility="{Binding IsRead,
                                           Converter={StaticResource BoolToVis}}"/>

                            <StackPanel Grid.Column="1">
                                <DockPanel>
                                    <TextBlock DockPanel.Dock="Right"
                                               Text="{Binding ReceivedAt,
                                                   StringFormat=HH:mm}"
                                               FontSize="10" Foreground="#636366"
                                               FontFamily="Segoe UI Variable"/>
                                    <TextBlock Text="{Binding SessionTitle}"
                                               FontSize="12" FontWeight="SemiBold"
                                               Foreground="#FFFFFF"
                                               FontFamily="Segoe UI Variable"
                                               TextTrimming="CharacterEllipsis"/>
                                </DockPanel>
                                <TextBlock Text="{Binding Body}"
                                           FontSize="11" Foreground="#8E8E93"
                                           FontFamily="Segoe UI Variable"
                                           TextTrimming="CharacterEllipsis"
                                           Margin="0,2,0,0"/>
                            </StackPanel>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </DockPanel>
</UserControl>
```

**코드비하인드**: `NotificationPanelControl.xaml.cs` — DataContext는 `MainWindowViewModel`에서 바인딩되므로 최소한.

### 4.4 MainWindowViewModel 확장

**추가 프로퍼티 + 커맨드**:

```csharp
// 알림 패널 상태
[ObservableProperty]
private bool _isNotificationPanelOpen;

[ObservableProperty]
private int _notificationPanelWidth;  // 0 or 280

[ObservableProperty]
private int _notificationDividerWidth;  // 0 or 1

// 서비스 참조
private readonly IOscNotificationService _oscService;

// IOscNotificationService.Notifications 직접 바인딩
public ObservableCollection<NotificationEntry> Notifications
    => _oscService.Notifications;

public int UnreadCount => _oscService.UnreadCount;
public bool HasUnread => _oscService.UnreadCount > 0;

[RelayCommand]
private void ToggleNotificationPanel()
{
    IsNotificationPanelOpen = !IsNotificationPanelOpen;
    NotificationPanelWidth = IsNotificationPanelOpen ? 280 : 0;
    NotificationDividerWidth = IsNotificationPanelOpen ? 1 : 0;
}

[RelayCommand]
private void NotificationClick(NotificationEntry entry)
{
    // 1. 읽음 처리
    _oscService.MarkAsRead(entry);
    // 2. 해당 워크스페이스로 전환
    var ws = _workspaceService.FindWorkspaceBySessionId(entry.SessionId);
    if (ws != null)
        _workspaceService.ActivateWorkspace(ws.Id);
    // 3. 알림 dismiss
    _oscService.DismissAttention(entry.SessionId);
}

[RelayCommand]
private void JumpToUnread()
{
    var entry = _oscService.GetMostRecentUnread();
    if (entry == null) return;
    NotificationClick(entry);
}

[RelayCommand]
private void MarkAllRead()
{
    _oscService.MarkAllAsRead();
}
```

**생성자에서 DI 추가**:

```csharp
public MainWindowViewModel(
    IWorkspaceService workspaceService,
    ISettingsService settingsService,
    IOscNotificationService oscService)  // Phase 6-B 추가
{
    _oscService = oscService;
    // OscNotificationService가 ObservableObject 상속하므로 PropertyChanged 구독
    if (oscService is INotifyPropertyChanged npc)
        npc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IOscNotificationService.UnreadCount))
            {
                OnPropertyChanged(nameof(UnreadCount));
                OnPropertyChanged(nameof(HasUnread));
            }
        };
    // ... 기존 코드 유지
}
```

### 4.5 키보드 바인딩 (MainWindow.xaml.cs)

기존 `OnTerminalKeyDown`의 `Ctrl+Shift` 블록에 추가:

```csharp
// 기존 Ctrl+Shift 블록 안 (line ~520)
if (IsCtrlDown() && IsShiftDown() && !IsAltDown())
{
    // ... 기존 C, V, W 처리 ...

    // Phase 6-B: 알림 패널 토글
    if (actualKey == Key.I)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ToggleNotificationPanelCommand.Execute(null);
        e.Handled = true;
        return;
    }

    // Phase 6-B: 미읽음 즉시 점프
    if (actualKey == Key.U)
    {
        if (DataContext is MainWindowViewModel vm2)
            vm2.JumpToUnreadCommand.Execute(null);
        e.Handled = true;
        return;
    }
}
```

### 4.6 InverseBoolToVisibilityConverter (신규)

**파일**: `src/GhostWin.App/Converters/InverseBoolToVisibilityConverter.cs`

```csharp
namespace GhostWin.App.Converters;

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

MainWindow.xaml Resources에 등록:
```xaml
<conv:InverseBoolToVisibilityConverter x:Key="InverseBoolToVis"/>
```

---

## 5. Wave 3 — AgentState 상태 전환 + 배지

### 5.1 상태 전환 로직 (SessionManager)

**파일**: `src/GhostWin.Services/SessionManager.cs`

기존 `SessionManager`에 출력 타임스탬프 기반 상태 전환 추가:

```csharp
// I/O 루프의 stdout 읽기 콜백 내부 (이미 존재하는 위치)
// ConPTY stdout 데이터 수신 시:
private void OnSessionOutput(uint sessionId, byte[] data)
{
    var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
    if (session == null) return;

    session.LastOutputTime = DateTimeOffset.UtcNow;

    if (session.AgentState is AgentState.Idle or AgentState.Completed or AgentState.Error)
    {
        session.AgentState = AgentState.Running;
        var ws = _workspaceService?.FindWorkspaceBySessionId(sessionId);
        if (ws != null) ws.AgentState = AgentState.Running;
    }
}
```

**Idle 전환 타이머**: 5초 무출력 시 Running → Idle.

```csharp
// DispatcherTimer (1초 주기, UI 스레드)
// MainWindow.xaml.cs의 기존 _cwdPollTimer 패턴과 동일
private DispatcherTimer? _agentStateTimer;

public void StartAgentStateTimer()
{
    _agentStateTimer = new DispatcherTimer(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    _agentStateTimer.Tick += (_, _) =>
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
        foreach (var s in Sessions)
        {
            if (s.AgentState == AgentState.Running && s.LastOutputTime < cutoff)
            {
                s.AgentState = AgentState.Idle;
                var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                if (ws != null) ws.AgentState = AgentState.Idle;
            }
        }
    };
    _agentStateTimer.Start();
}
```

**타이머 시작 시점**: `MainWindow.InitializeRenderer()`에서 `_cwdPollTimer.Start()` 직후.

```csharp
// MainWindow.xaml.cs InitializeRenderer() 끝부분
if (_sessionManager is SessionManager sm2)
    sm2.StartAgentStateTimer();
```

**프로세스 종료 시 상태 전환**:

기존 `OnChildExit` 콜백(`MainWindow.xaml.cs:181-188`)에서 `CloseSession` 호출 전:

```csharp
OnChildExit = (id, code) =>
{
    if (_shuttingDown) return;
    // Phase 6-B: exit code 기반 상태 전환
    var session = _sessionManager.Sessions.FirstOrDefault(s => s.Id == id);
    if (session != null)
    {
        session.AgentState = code == 0 ? AgentState.Completed : AgentState.Error;
        var ws = _workspaceService.FindWorkspaceBySessionId(id);
        if (ws != null) ws.AgentState = session.AgentState;
    }
    _sessionManager.CloseSession(id);
    if (_sessionManager.Sessions.Count == 0)
        this.Close();
},
```

### 5.2 사이드바 배지 XAML

기존 `MainWindow.xaml`의 workspace DataTemplate 내 `StackPanel`(line 288-303)에 배지 추가:

```xaml
<StackPanel Orientation="Horizontal">
    <TextBlock Text="{Binding Title}" ... />

    <!-- Phase 6-A: amber dot (기존, NeedsAttention 바인딩) -->
    <Ellipse Width="8" Height="8" Fill="#FFB020" ... />

    <!-- Phase 6-B: 에이전트 상태 배지 -->
    <TextBlock Text="{Binding AgentStateBadge}"
               FontSize="10" Margin="4,0,0,0"
               Foreground="{Binding AgentStateColor}"
               VerticalAlignment="Center"
               FontFamily="Segoe UI Variable"
               Visibility="{Binding ShowAgentBadge,
                   Converter={StaticResource BoolToVisibility}}"
               AutomationProperties.AutomationId="{Binding WorkspaceId,
                   StringFormat=E2E_AgentBadge_{0}}"/>
</StackPanel>
```

### 5.3 WorkspaceItemViewModel 확장

```csharp
// 기존 프로퍼티에 추가
public AgentState AgentState => _workspace.AgentState;

public string AgentStateBadge => _workspace.AgentState switch
{
    AgentState.Running => "●",
    AgentState.WaitingForInput => "●",
    AgentState.Error => "✕",
    AgentState.Completed => "✓",
    _ => ""
};

public SolidColorBrush AgentStateColor => _workspace.AgentState switch
{
    AgentState.Running => new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),       // 초록
    AgentState.WaitingForInput => new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF)), // 파란
    AgentState.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),           // 빨간
    AgentState.Completed => new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),       // 회색
    _ => Brushes.Transparent
};

public bool ShowAgentBadge => _workspace.AgentState != AgentState.Idle;
```

`OnWorkspacePropertyChanged`는 이미 모든 `PropertyChanged` 이벤트를 중계하므로, `AgentState` 변경 시 `AgentStateBadge`, `AgentStateColor`, `ShowAgentBadge`도 갱신 필요:

```csharp
private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    OnPropertyChanged(e.PropertyName);
    if (e.PropertyName == nameof(WorkspaceInfo.AgentState))
    {
        OnPropertyChanged(nameof(AgentStateBadge));
        OnPropertyChanged(nameof(AgentStateColor));
        OnPropertyChanged(nameof(ShowAgentBadge));
    }
}
```

---

## 6. Wave 4 — Toast 클릭 액션

### 6.1 Toast 생성 변경

**파일**: `src/GhostWin.App/App.xaml.cs` (line 102-115)

```csharp
// 기존 Toast 생성을 인자 포함으로 변경
WeakReferenceMessenger.Default.Register<OscNotificationMessage>(this,
    (_, msg) =>
    {
        if (MainWindow?.IsActive == true) return;
        if (!settingsSvc.Current.Notifications.ToastEnabled) return;
        try
        {
            new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddArgument("action", "switchTab")
                .AddArgument("sessionId", msg.SessionId.ToString())
                .AddText(string.IsNullOrEmpty(msg.Title) ? "GhostWin" : msg.Title)
                .AddText(string.IsNullOrEmpty(msg.Body) ? msg.Title : msg.Body)
                .Show();
        }
        catch { }
    });
```

### 6.2 Toast 클릭 핸들러

**파일**: `src/GhostWin.App/App.xaml.cs` — `OnStartup` 내, Toast 등록 직후 추가

```csharp
// Phase 6-B: Toast 클릭 → 탭 전환
Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += args =>
{
    var parsed = Microsoft.Toolkit.Uwp.Notifications.ToastArguments.Parse(args.Argument);
    if (!parsed.TryGetValue("sessionId", out var idStr) ||
        !uint.TryParse(idStr, out var sessionId))
        return;

    Dispatcher.BeginInvoke(() =>
    {
        // 1. 창 복원 + 활성화
        if (MainWindow is Window w)
        {
            if (w.WindowState == WindowState.Minimized)
                w.WindowState = WindowState.Normal;
            w.Activate();
        }

        // 2. 해당 세션의 워크스페이스로 전환
        var ws = wsSvc.FindWorkspaceBySessionId(sessionId);
        if (ws != null)
            wsSvc.ActivateWorkspace(ws.Id);

        // 3. 알림 dismiss
        var oscSvc = Ioc.Default.GetService<IOscNotificationService>();
        oscSvc?.DismissAttention(sessionId);
    });
};
```

**엣지 케이스 처리**:
- `sessionId`가 유효하지 않은 경우 (탭 이미 닫힘): `FindWorkspaceBySessionId` → null → 무시
- 앱 종료 후 Toast 클릭: `OnActivated`가 호출되지 않음 (v1에서는 무시)

---

## 7. 전체 스레드 다이어그램

```
I/O Thread (ConPTY read)
  │
  ├── ghostty VtDesktopNotifyFn 콜백 발사
  │
  ▼
Dispatcher.BeginInvoke (UI Thread)
  │
  ├── OscNotificationService.HandleOscEvent()
  │     ├── SessionInfo.NeedsAttention = true
  │     ├── SessionInfo.AgentState = WaitingForInput
  │     ├── WorkspaceInfo 미러링
  │     ├── Notifications.Insert(0, entry)  ← Phase 6-B (UI thread)
  │     └── Messenger.Send(OscNotificationMessage)
  │           ├── App.xaml.cs → Toast 발사 (+ sessionId 인자)
  │           └── (미래 구독자)
  │
  ├── PropertyChanged 이벤트 전파
  │     ├── WorkspaceItemViewModel → AgentStateBadge/Color 갱신
  │     ├── MainWindow.xaml DataTrigger → amber dot / 배지 표시
  │     └── NotificationPanelControl ListBox 갱신
  │
  └── (별도) AgentState 타이머 (1초)
        └── Running + 5초 무출력 → Idle 전환

Toast 클릭 (백그라운드 스레드)
  │
  └── Dispatcher.BeginInvoke
        ├── Window.Activate()
        ├── ActivateWorkspace(wsId)
        └── DismissAttention(sessionId)
```

---

## 8. 파일 변경 목록 (최종)

### 신규 파일 (4개)

| 파일 | 프로젝트 | 내용 |
|------|---------|------|
| `AgentState.cs` | GhostWin.Core/Models | 에이전트 상태 enum (5값) |
| `NotificationEntry.cs` | GhostWin.Core/Models | 알림 항목 ObservableObject |
| `NotificationPanelControl.xaml` | GhostWin.App/Controls | 알림 패널 UI |
| `InverseBoolToVisibilityConverter.cs` | GhostWin.App/Converters | !bool → Visibility |

### 변경 파일 (10개)

| 파일 | 변경 내용 |
|------|----------|
| `IOscNotificationService.cs` | Notifications 컬렉션, UnreadCount, Mark 메서드 추가 |
| `OscNotificationService.cs` | ObservableObject 상속, 히스토리 관리, AgentState 연동 |
| `SessionInfo.cs` | AgentState, LastOutputTime 프로퍼티 추가 |
| `WorkspaceInfo.cs` | AgentState 프로퍼티 추가 |
| `WorkspaceItemViewModel.cs` | AgentStateBadge/Color/Show 계산 프로퍼티, PropertyChanged 확장 |
| `MainWindow.xaml` | Grid Column 추가 (알림 패널), 배지 TextBlock 추가, InverseBoolToVis 리소스 |
| `MainWindow.xaml.cs` | Ctrl+Shift+I/U 키 바인딩, AgentState 타이머 시작, OnChildExit 확장 |
| `MainWindowViewModel.cs` | IOscNotificationService DI, 패널 토글/클릭/점프/읽음 커맨드 |
| `AppSettings.cs` | NotificationSettings에 PanelEnabled, BadgeEnabled 추가 |
| `App.xaml.cs` | Toast AddArgument, OnActivated 핸들러 |

**총계**: 신규 4 + 변경 10 = **14개 파일**

---

## 9. 검증 계획

### 9.1 수동 검증

| # | 시나리오 | 검증 방법 | 예상 결과 |
|:-:|---------|----------|----------|
| T-1 | Ctrl+Shift+I 토글 | 키 입력 | 패널 열림/닫힘 (280px ↔ 0px) |
| T-2 | OSC 9 → 패널 항목 | `Write-Host "\e]9;테스트\e\"` | 패널에 시간+메시지 표시 |
| T-3 | 패널 항목 클릭 → 탭 전환 | 마우스 클릭 | 해당 탭 활성화 + 읽음 처리 |
| T-4 | Ctrl+Shift+U | 키 입력 | 최근 미읽음 탭으로 즉시 전환 |
| T-5 | 배지 Running | 터미널에서 명령 실행 | 초록 ● 표시 |
| T-6 | 배지 WaitingForInput | OSC 9 주입 | 파란 ● 표시 |
| T-7 | 배지 Idle | 5초 대기 | 배지 사라짐 |
| T-8 | Toast 클릭→탭 전환 | 창 비활성 → OSC → Toast 클릭 | GhostWin 활성화 + 해당 탭 |
| T-9 | 100건 초과 | OSC 100+ 주입 | FIFO, 가장 오래된 것 제거 |
| T-10 | Mark All Read | 버튼 클릭 | 모든 항목 읽음 |

### 9.2 AutomationId 목록 (E2E 대비)

| AutomationId | 요소 | 용도 |
|--------------|------|------|
| `E2E_NotificationList` | 알림 패널 ListBox | 알림 항목 검증 |
| `E2E_MarkAllRead` | 모두 읽음 버튼 | 버튼 클릭 자동화 |
| `E2E_AgentBadge_{id}` | 사이드바 배지 | 배지 상태 검증 |

---

## 10. 의도적 간소화 (Design에서 명시)

Phase 6-A Lessons Learned 적용: 구현 시 발견되는 "설계와 다른 점"을 줄이기 위해 미리 명시.

| # | 항목 | 간소화 내용 | 근거 |
|:-:|------|-----------|------|
| S-1 | 알림 히스토리 저장 | 인메모리만 (SQLite 없음) | 앱 재시작 시 알림 초기화. 복잡도 대비 가치 낮음 |
| S-2 | 패널 애니메이션 | Width 직접 설정 (DoubleAnimation 없음) | v1에서는 즉시 열림/닫힘. 부드러운 애니메이션은 추후 |
| S-3 | AgentState stdin 감지 | 미구현 | 사용자 입력 감지는 복잡 (TSF, 키 이벤트 등). stdout 기반으로 충분 |
| S-4 | 알림 소리 | 미구현 | Windows 터미널 관례상 불필요. 사용자 요청 시 추가 |
| S-5 | OnActivated 앱 미실행 | 무시 | v1 범위 밖 |

---

## 참조

- **Plan**: `docs/01-plan/features/phase-6-b-notification-infra.plan.md`
- **Phase 6-A Design**: `docs/archive/2026-04/phase-6-a-osc-notification-ring/phase-6-a-osc-notification-ring.design.md`
- **Phase 6-A Report**: `docs/archive/2026-04/phase-6-a-osc-notification-ring/phase-6-a-osc-notification-ring.report.md`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md`

---

*Phase 6-B Design v1.0 — Notification Infrastructure (2026-04-16)*
