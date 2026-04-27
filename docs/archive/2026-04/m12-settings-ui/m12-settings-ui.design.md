# Design — M-12: 사용자 설정 UI (Settings UI)

> **문서 종류**: Design (구현 명세)
> **작성일**: 2026-04-17
> **Plan 참조**: `docs/01-plan/features/m12-settings-ui.plan.md`
> **PRD 참조**: `docs/00-pm/m12-settings-ui.prd.md`

---

## 1. 한 줄 요약

터미널 영역(Grid Column 4)을 `SettingsPageControl`로 대체하는 페이지 전환 패턴 + `CommandPaletteWindow`(별도 최상위 Window, Airspace 우회)로 GUI 설정 제공. 기존 `ISettingsService`/`AppSettings`/`UpdateCellMetrics` 100% 재사용.

---

## 2. 구현 순서 (7 Waves)

| Wave | 범위 | 의존 | 검증 |
|:----:|------|:---:|------|
| **W1** | SettingsPageViewModel + AppSettings 양방향 바인딩 | — | 빌드 성공 |
| **W2** | SettingsPageControl XAML (4개 카테고리 섹션) | W1 | 수동: 페이지 렌더링 |
| **W3** | 페이지 전환 (Ctrl+, / Esc / 기어 아이콘) + 포커스 관리 | W2 | 수동: 전환 + 포커스 복원 |
| **W4** | 폰트 변경 → UpdateCellMetrics + Save debounce + 루프 방지 | W3 | 수동: 슬라이더 → 폰트 변경 |
| **W5** | 키바인딩 편집 UI | W3 | 수동: 키 변경 → 동작 확인 |
| **W6** | CommandPaletteWindow (별도 Window + 퍼지 검색) | — | 수동: Ctrl+Shift+P |
| **W7** | "JSON으로 편집" + 통합 검증 | W4 | JSON↔GUI 양방향 확인 |

---

## 3. Wave 1 — SettingsPageViewModel

### 3.1 ViewModel (신규)

**파일**: `src/GhostWin.App/ViewModels/SettingsPageViewModel.cs`

```csharp
public partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private Timer? _saveDebounce;
    private bool _suppressSave;

    // ── 외관 ──
    [ObservableProperty] private string _appearance = "dark";
    [ObservableProperty] private bool _useMica = true;

    // ── 폰트 ──
    [ObservableProperty] private string _fontFamily = "Cascadia Mono";
    [ObservableProperty] private double _fontSize = 14.0;
    [ObservableProperty] private double _cellWidthScale = 1.0;
    [ObservableProperty] private double _cellHeightScale = 1.0;

    // ── 사이드바 ──
    [ObservableProperty] private bool _sidebarVisible = true;
    [ObservableProperty] private int _sidebarWidth = 200;
    [ObservableProperty] private bool _showCwd = true;
    [ObservableProperty] private bool _showGit = true;

    // ── 알림 ──
    [ObservableProperty] private bool _ringEnabled = true;
    [ObservableProperty] private bool _toastEnabled = true;
    [ObservableProperty] private bool _panelEnabled = true;
    [ObservableProperty] private bool _badgeEnabled = true;

    // 시스템 폰트 목록
    public ObservableCollection<string> AvailableFonts { get; } = [];

    public SettingsPageViewModel(ISettingsService settings)
    {
        _settings = settings;
        LoadFromSettings(settings.Current);
        LoadSystemFonts();
    }

    public void LoadFromSettings(AppSettings s)
    {
        _suppressSave = true;
        Appearance = s.Appearance;
        UseMica = s.Titlebar.UseMica;
        FontFamily = s.Terminal.Font.Family;
        FontSize = s.Terminal.Font.Size;
        CellWidthScale = s.Terminal.Font.CellWidthScale;
        CellHeightScale = s.Terminal.Font.CellHeightScale;
        SidebarVisible = s.Sidebar.Visible;
        SidebarWidth = s.Sidebar.Width;
        ShowCwd = s.Sidebar.ShowCwd;
        ShowGit = s.Sidebar.ShowGit;
        RingEnabled = s.Notifications.RingEnabled;
        ToastEnabled = s.Notifications.ToastEnabled;
        PanelEnabled = s.Notifications.PanelEnabled;
        BadgeEnabled = s.Notifications.BadgeEnabled;
        _suppressSave = false;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressSave)
            ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyAndSave());
        }, null, 300, Timeout.Infinite);
    }

    private void ApplyAndSave()
    {
        var s = _settings.Current;
        s.Appearance = Appearance;
        s.Titlebar.UseMica = UseMica;
        s.Terminal.Font.Family = FontFamily;
        s.Terminal.Font.Size = FontSize;
        s.Terminal.Font.CellWidthScale = CellWidthScale;
        s.Terminal.Font.CellHeightScale = CellHeightScale;
        s.Sidebar.Visible = SidebarVisible;
        s.Sidebar.Width = SidebarWidth;
        s.Sidebar.ShowCwd = ShowCwd;
        s.Sidebar.ShowGit = ShowGit;
        s.Notifications.RingEnabled = RingEnabled;
        s.Notifications.ToastEnabled = ToastEnabled;
        s.Notifications.PanelEnabled = PanelEnabled;
        s.Notifications.BadgeEnabled = BadgeEnabled;
        _settings.Save();
    }

    private void LoadSystemFonts()
    {
        foreach (var family in Fonts.SystemFontFamilies
                     .OrderBy(f => f.Source))
            AvailableFonts.Add(family.Source);
    }
}
```

**핵심 설계**:
- `_suppressSave`: `LoadFromSettings` 중 PropertyChanged → Save 방지
- `ScheduleSave`: 300ms debounce. 슬라이더 드래그 중 연속 Save 방지
- `ApplyAndSave`: `_settings.Current` 직접 갱신 → `Save()` 호출

### 3.2 SettingsService 자기 루프 방지

**파일**: `src/GhostWin.Services/SettingsService.cs` — 변경

```csharp
private bool _suppressWatcher;

public void Save()
{
    _suppressWatcher = true;
    try
    {
        // ... 기존 Save 로직 ...
    }
    finally
    {
        // 100ms 후 watcher 재활성화 (FileSystemWatcher 이벤트 전파 지연 대비)
        Task.Delay(100).ContinueWith(_ => _suppressWatcher = false);
    }
}

private void OnFileChanged(object sender, FileSystemEventArgs e)
{
    if (_suppressWatcher) return;  // 자기 Save 감지 무시
    // ... 기존 debounce 로직 ...
}
```

---

## 4. Wave 2 — SettingsPageControl XAML

### 4.1 설정 페이지 UserControl (신규)

**파일**: `src/GhostWin.App/Controls/SettingsPageControl.xaml`

```xaml
<UserControl x:Class="GhostWin.App.Controls.SettingsPageControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="#1A1A1A"
             Focusable="True">

    <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="32,24">
        <StackPanel MaxWidth="680">

            <!-- Header -->
            <DockPanel Margin="0,0,0,24">
                <Button DockPanel.Dock="Left" Content="←" FontSize="16"
                        Background="Transparent" BorderThickness="0"
                        Foreground="#FFFFFF" Cursor="Hand"
                        Command="{Binding CloseSettingsCommand}"
                        Focusable="False"/>
                <TextBlock Text="Settings" FontSize="20" FontWeight="SemiBold"
                           Foreground="#FFFFFF" FontFamily="Segoe UI Variable"
                           VerticalAlignment="Center" Margin="12,0,0,0"/>
                <Button DockPanel.Dock="Right" Content="✕" FontSize="14"
                        Background="Transparent" BorderThickness="0"
                        Foreground="#8E8E93" Cursor="Hand" HorizontalAlignment="Right"
                        Command="{Binding CloseSettingsCommand}"
                        Focusable="False"/>
            </DockPanel>

            <!-- ═══ 외관 ═══ -->
            <TextBlock Text="APPEARANCE" Style="{StaticResource SectionHeader}"/>
            <Border Style="{StaticResource SettingsCard}">
                <StackPanel>
                    <!-- 테마 -->
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Theme" Style="{StaticResource SettingLabel}"/>
                        <ComboBox DockPanel.Dock="Right" Width="160"
                                  SelectedItem="{Binding Appearance}"
                                  AutomationProperties.AutomationId="E2E_ThemeCombo">
                            <ComboBoxItem Content="dark"/>
                            <ComboBoxItem Content="light"/>
                        </ComboBox>
                    </DockPanel>
                    <!-- Mica -->
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Mica backdrop" Style="{StaticResource SettingLabel}"/>
                        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                            <CheckBox IsChecked="{Binding UseMica}"/>
                            <TextBlock Text="(restart required)" FontSize="10"
                                       Foreground="#636366" Margin="8,0,0,0"
                                       VerticalAlignment="Center"/>
                        </StackPanel>
                    </DockPanel>
                </StackPanel>
            </Border>

            <!-- ═══ 폰트 ═══ -->
            <TextBlock Text="FONT" Style="{StaticResource SectionHeader}"/>
            <Border Style="{StaticResource SettingsCard}">
                <StackPanel>
                    <!-- 패밀리 -->
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Family" Style="{StaticResource SettingLabel}"/>
                        <ComboBox DockPanel.Dock="Right" Width="200"
                                  ItemsSource="{Binding AvailableFonts}"
                                  SelectedItem="{Binding FontFamily}"
                                  IsEditable="True"
                                  AutomationProperties.AutomationId="E2E_FontFamily"/>
                    </DockPanel>
                    <!-- 크기 -->
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Size" Style="{StaticResource SettingLabel}"/>
                        <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                            <Slider Width="150" Minimum="8" Maximum="36"
                                    Value="{Binding FontSize}"
                                    TickFrequency="1" IsSnapToTickEnabled="True"
                                    AutomationProperties.AutomationId="E2E_FontSize"/>
                            <TextBlock Text="{Binding FontSize, StringFormat={}{0:F0}pt}"
                                       Width="40" Foreground="#8E8E93" Margin="8,0,0,0"
                                       VerticalAlignment="Center"/>
                        </StackPanel>
                    </DockPanel>
                    <!-- 셀 비율 -->
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Cell width scale" Style="{StaticResource SettingLabel}"/>
                        <Slider DockPanel.Dock="Right" Width="150"
                                Minimum="0.5" Maximum="2.0"
                                Value="{Binding CellWidthScale}"
                                TickFrequency="0.1" IsSnapToTickEnabled="True"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Cell height scale" Style="{StaticResource SettingLabel}"/>
                        <Slider DockPanel.Dock="Right" Width="150"
                                Minimum="0.5" Maximum="2.0"
                                Value="{Binding CellHeightScale}"
                                TickFrequency="0.1" IsSnapToTickEnabled="True"/>
                    </DockPanel>
                </StackPanel>
            </Border>

            <!-- ═══ 사이드바 ═══ -->
            <TextBlock Text="SIDEBAR" Style="{StaticResource SectionHeader}"/>
            <Border Style="{StaticResource SettingsCard}">
                <StackPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Visible" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding SidebarVisible}"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Width" Style="{StaticResource SettingLabel}"/>
                        <Slider DockPanel.Dock="Right" Width="150"
                                Minimum="120" Maximum="400"
                                Value="{Binding SidebarWidth}"
                                TickFrequency="10" IsSnapToTickEnabled="True"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Show CWD" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding ShowCwd}"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Show git info" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding ShowGit}"/>
                    </DockPanel>
                </StackPanel>
            </Border>

            <!-- ═══ 알림 (Phase 6) ═══ -->
            <TextBlock Text="NOTIFICATIONS" Style="{StaticResource SectionHeader}"/>
            <Border Style="{StaticResource SettingsCard}">
                <StackPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Notification ring" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding RingEnabled}"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Toast alerts" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding ToastEnabled}"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Notification panel" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding PanelEnabled}"/>
                    </DockPanel>
                    <DockPanel Style="{StaticResource SettingRow}">
                        <TextBlock Text="Agent status badge" Style="{StaticResource SettingLabel}"/>
                        <CheckBox DockPanel.Dock="Right" IsChecked="{Binding BadgeEnabled}"/>
                    </DockPanel>
                </StackPanel>
            </Border>

            <!-- ═══ JSON 편집 링크 ═══ -->
            <Button Content="Open JSON file" FontSize="12"
                    Foreground="#0091FF" Background="Transparent"
                    BorderThickness="0" Cursor="Hand" Margin="0,24,0,0"
                    HorizontalAlignment="Left"
                    Command="{Binding OpenJsonCommand}"
                    Focusable="False"/>

        </StackPanel>
    </ScrollViewer>
</UserControl>
```

### 4.2 XAML 스타일 리소스 (설정 페이지 전용)

`SettingsPageControl.xaml`의 `UserControl.Resources`에 추가:

```xaml
<Style x:Key="SectionHeader" TargetType="TextBlock">
    <Setter Property="FontSize" Value="11"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Foreground" Value="#8E8E93"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable"/>
    <Setter Property="Margin" Value="0,24,0,8"/>
</Style>

<Style x:Key="SettingsCard" TargetType="Border">
    <Setter Property="Background" Value="#2C2C2E"/>
    <Setter Property="CornerRadius" Value="8"/>
    <Setter Property="Padding" Value="16"/>
    <Setter Property="Margin" Value="0,0,0,4"/>
</Style>

<Style x:Key="SettingRow" TargetType="DockPanel">
    <Setter Property="Margin" Value="0,6"/>
</Style>

<Style x:Key="SettingLabel" TargetType="TextBlock">
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Foreground" Value="#FFFFFF"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
</Style>
```

---

## 5. Wave 3 — 페이지 전환 + 포커스 관리

### 5.1 MainWindow.xaml 변경

터미널 영역(Column 4)에 설정 페이지를 겹쳐 배치하고 Visibility로 전환:

```xaml
<!-- ═══ Terminal area ═══ -->
<Border Grid.Column="4" Background="{StaticResource TerminalBg}">
    <!-- 터미널 (설정 닫힘 시 표시) -->
    <controls:PaneContainerControl x:Name="PaneContainer"
        Visibility="{Binding IsSettingsOpen,
            Converter={StaticResource InverseBoolToVis}}"/>
</Border>

<!-- 설정 페이지 (설정 열림 시 표시, 같은 Column 4) -->
<controls:SettingsPageControl Grid.Column="4"
    Visibility="{Binding IsSettingsOpen,
        Converter={StaticResource BoolToVisibility}}"
    DataContext="{Binding SettingsPageVM}"/>
```

**핵심**: PaneContainerControl과 SettingsPageControl이 **같은 Grid.Column=4**에 배치. `IsSettingsOpen` 바인딩으로 한쪽만 보임. D3D11 SwapChain은 PaneContainer가 Collapsed되면 렌더링 중단.

### 5.2 MainWindowViewModel 확장

```csharp
[ObservableProperty]
private bool _isSettingsOpen;

[ObservableProperty]
private SettingsPageViewModel? _settingsPageVM;

[RelayCommand]
private void OpenSettings()
{
    if (SettingsPageVM == null)
        SettingsPageVM = new SettingsPageViewModel(_settingsService);
    else
        SettingsPageVM.LoadFromSettings(_settingsService.Current);
    IsSettingsOpen = true;
}

[RelayCommand]
private void CloseSettings()
{
    IsSettingsOpen = false;
    // 포커스 복원은 MainWindow.xaml.cs에서 처리
}
```

### 5.3 키바인딩 (MainWindow.xaml.cs)

```csharp
// Ctrl+, → 설정 열기
if (IsCtrlDown() && !IsShiftDown() && !IsAltDown())
{
    if (e.Key == Key.OemComma)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenSettingsCommand.Execute(null);
        e.Handled = true;
        return;
    }
}

// Esc → 설정 닫기 (설정 열려있을 때만)
if (e.Key == Key.Escape)
{
    if (DataContext is MainWindowViewModel { IsSettingsOpen: true } vm)
    {
        vm.CloseSettingsCommand.Execute(null);
        // 포커스 복원: 활성 pane의 TerminalHostControl로
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            PaneContainer.GetFocusedHost()?.Focus();
        });
        e.Handled = true;
        return;
    }
}

// Ctrl+Shift+P → Command Palette
if (IsCtrlDown() && IsShiftDown() && !IsAltDown() && actualKey == Key.P)
{
    ShowCommandPalette();
    e.Handled = true;
    return;
}
```

### 5.4 사이드바 기어 아이콘

사이드바 하단에 설정 아이콘 추가:

```xaml
<!-- 사이드바 DockPanel 하단 (ListBox 아래) -->
<Button DockPanel.Dock="Bottom" Content="⚙" FontSize="16"
        Background="Transparent" BorderThickness="0"
        Foreground="#636366" Cursor="Hand"
        HorizontalAlignment="Left" Margin="16,8"
        Command="{Binding OpenSettingsCommand}"
        ToolTip="Settings (Ctrl+,)"
        Focusable="False" IsTabStop="False"/>
```

### 5.5 포커스 복원 전략

```
설정 열기:
  1. IsSettingsOpen = true
  2. PaneContainer.Visibility = Collapsed → HwndHost 포커스 자동 해제
  3. SettingsPageControl.Visibility = Visible
  4. SettingsPageControl 내부 ScrollViewer에 키보드 포커스 자동 이동

설정 닫기:
  1. IsSettingsOpen = false
  2. SettingsPageControl.Visibility = Collapsed
  3. PaneContainer.Visibility = Visible → HwndHost 재표시
  4. Dispatcher.BeginInvoke(Input) → PaneContainer.GetFocusedHost()?.Focus()
     → TerminalHostControl.OnGotFocus → Win32 SetFocus(childHwnd)
  5. 키보드 입력이 터미널에 정상 전달됨
```

**위험 시나리오**: PaneContainer가 Collapsed→Visible 되면서 HwndHost가 재생성될 수 있음.
**대응**: HwndHost는 Visibility 변경 시 HWND를 유지함 (DestroyWindow 호출 안 함). 확인 필요.

---

## 6. Wave 4 — 폰트 변경 → UpdateCellMetrics

### 6.1 SettingsChangedMessage 수신 시 UpdateCellMetrics 호출

이미 `MainWindow.xaml.cs`의 `OnDpiChanged`에서 `UpdateCellMetrics`를 호출하는 패턴이 존재합니다. SettingsChanged 시에도 동일 호출:

```csharp
// MainWindowViewModel.Receive(SettingsChangedMessage) 또는
// MainWindow.xaml.cs에서 직접 구독
WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(mainWindow,
    (_, msg) =>
    {
        // 폰트 변경 시 UpdateCellMetrics 호출
        var font = msg.Value.Terminal.Font;
        var dpiScale = (float)VisualTreeHelper.GetDpi(mainWindow).DpiScaleX;
        _engine.UpdateCellMetrics(
            (float)font.Size, font.Family, dpiScale,
            (float)font.CellWidthScale, (float)font.CellHeightScale, 1.0f);
    });
```

### 6.2 SettingsPageViewModel → FileWatcher → SettingsChanged 경로 (양방향)

```
GUI 편집 (SettingsPageViewModel)
  ↓ PropertyChanged
  ↓ 300ms debounce
  ↓ ApplyAndSave()
  ↓ SettingsService.Save() (_suppressWatcher=true)
  ↓ ghostwin.json 업데이트
  ↓ FileWatcher 감지 → _suppressWatcher=true → 무시 (자기 루프 방지)
  ↓ _suppressWatcher=false (100ms 후)
  ✓ 루프 없음

JSON 수동 편집 (외부 에디터)
  ↓ ghostwin.json 변경
  ↓ FileWatcher 감지 → _suppressWatcher=false → 처리
  ↓ Load() → Current 갱신
  ↓ OnSettingsReloaded → Dispatcher → SettingsChangedMessage
  ↓ MainWindowViewModel.ApplySettings()
  ↓ SettingsPageViewModel.LoadFromSettings() (_suppressSave=true)
  ✓ 루프 없음
```

### 6.3 SettingsPageViewModel ↔ SettingsChangedMessage 동기화

`MainWindowViewModel.Receive(SettingsChangedMessage)`에서 설정 페이지 ViewModel도 갱신:

```csharp
public void Receive(SettingsChangedMessage msg)
{
    ApplySettings(msg.Value);
    // 설정 페이지가 열려있으면 GUI도 갱신 (JSON 수동 편집 반영)
    SettingsPageVM?.LoadFromSettings(msg.Value);
}
```

---

## 7. Wave 5 — 키바인딩 편집 UI

v1에서는 **단순 TextBox 표시 + 키 캡처** 방식:

```xaml
<!-- 키바인딩 섹션 -->
<TextBlock Text="KEYBINDINGS" Style="{StaticResource SectionHeader}"/>
<Border Style="{StaticResource SettingsCard}">
    <ItemsControl ItemsSource="{Binding KeybindingEntries}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <DockPanel Style="{StaticResource SettingRow}">
                    <TextBlock Text="{Binding ActionName}"
                               Style="{StaticResource SettingLabel}"/>
                    <TextBox DockPanel.Dock="Right" Width="150"
                             Text="{Binding KeyCombo}"
                             IsReadOnly="True"
                             PreviewKeyDown="OnKeybindCapture"
                             GotFocus="OnKeybindFocused"/>
                </DockPanel>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</Border>
```

키 캡처 로직 (`SettingsPageControl.xaml.cs`):

```csharp
private void OnKeybindCapture(object sender, KeyEventArgs e)
{
    if (sender is not TextBox tb || tb.DataContext is not KeybindingEntry entry) return;

    var modifiers = Keyboard.Modifiers;
    var key = e.Key == Key.System ? e.SystemKey : e.Key;

    // 단독 modifier 키는 무시
    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        return;

    var parts = new List<string>();
    if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
    if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
    if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
    parts.Add(key.ToString());

    entry.KeyCombo = string.Join("+", parts);
    e.Handled = true;
}
```

---

## 8. Wave 6 — Command Palette

### 8.1 CommandInfo 모델 (신규)

**파일**: `src/GhostWin.Core/Models/CommandInfo.cs`

```csharp
public record CommandInfo(
    string ActionId,     // "CreateWorkspace", "ToggleNotificationPanel" 등
    string Name,         // "New workspace", "Toggle notification panel"
    string? KeyCombo,    // "Ctrl+T", "Ctrl+Shift+I"
    Action Execute);     // 실행 콜백
```

### 8.2 CommandPaletteWindow (신규)

**파일**: `src/GhostWin.App/CommandPaletteWindow.xaml`

```xaml
<Window x:Class="GhostWin.App.CommandPaletteWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" ShowInTaskbar="False"
        SizeToContent="Height" Width="500"
        WindowStartupLocation="CenterOwner"
        PreviewKeyDown="OnPreviewKeyDown">

    <Border Background="#2C2C2E" CornerRadius="8"
            BorderBrush="#3A3A3C" BorderThickness="1"
            Margin="0,80,0,0" VerticalAlignment="Top"
            MaxHeight="400">
        <StackPanel>
            <!-- 검색 TextBox -->
            <TextBox x:Name="SearchBox" FontSize="14"
                     Background="#1A1A1A" Foreground="#FFFFFF"
                     BorderThickness="0" Padding="12,10"
                     FontFamily="Segoe UI Variable"
                     TextChanged="OnSearchTextChanged"/>

            <!-- 결과 리스트 -->
            <ListBox x:Name="ResultList"
                     Background="Transparent" BorderThickness="0"
                     MaxHeight="300"
                     Focusable="False" IsTabStop="False"
                     MouseDoubleClick="OnResultDoubleClick">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <DockPanel Padding="12,6">
                            <TextBlock DockPanel.Dock="Right"
                                       Text="{Binding KeyCombo}"
                                       FontSize="11" Foreground="#636366"
                                       FontFamily="Segoe UI Variable"/>
                            <TextBlock Text="{Binding Name}"
                                       FontSize="13" Foreground="#FFFFFF"
                                       FontFamily="Segoe UI Variable"/>
                        </DockPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </StackPanel>
    </Border>
</Window>
```

### 8.3 CommandPaletteWindow 코드비하인드

```csharp
public partial class CommandPaletteWindow : Window
{
    private readonly List<CommandInfo> _allCommands;

    public CommandPaletteWindow(List<CommandInfo> commands)
    {
        InitializeComponent();
        _allCommands = commands;
        ResultList.ItemsSource = _allCommands;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            ResultList.ItemsSource = _allCommands;
            return;
        }
        ResultList.ItemsSource = _allCommands.Where(c =>
            c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.ActionId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        if (e.Key == Key.Enter && ResultList.SelectedItem is CommandInfo cmd)
        {
            Close();
            cmd.Execute();
            e.Handled = true;
        }
        if (e.Key == Key.Down) { ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1); e.Handled = true; }
        if (e.Key == Key.Up) { ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0); e.Handled = true; }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is CommandInfo cmd)
        {
            Close();
            cmd.Execute();
        }
    }
}
```

### 8.4 MainWindow에서 Command Palette 호출

```csharp
private void ShowCommandPalette()
{
    var vm = DataContext as MainWindowViewModel;
    if (vm == null) return;

    var commands = new List<CommandInfo>
    {
        new("CreateWorkspace", "New workspace", "Ctrl+T",
            () => _workspaceService.CreateWorkspace()),
        new("CloseWorkspace", "Close workspace", "Ctrl+W",
            () => { if (_workspaceService.ActiveWorkspaceId is {} id) _workspaceService.CloseWorkspace(id); }),
        new("SplitVertical", "Split vertical", "Alt+V",
            () => _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical)),
        new("SplitHorizontal", "Split horizontal", "Alt+H",
            () => _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Horizontal)),
        new("ToggleNotificationPanel", "Toggle notification panel", "Ctrl+Shift+I",
            () => vm.ToggleNotificationPanelCommand.Execute(null)),
        new("JumpToUnread", "Jump to unread", "Ctrl+Shift+U",
            () => vm.JumpToUnreadCommand.Execute(null)),
        new("OpenSettings", "Open settings", "Ctrl+,",
            () => vm.OpenSettingsCommand.Execute(null)),
        new("ToggleTheme", "Toggle theme", null,
            () => { vm.SettingsPageVM ??= new(Ioc.Default.GetRequiredService<ISettingsService>());
                     vm.SettingsPageVM.Appearance = vm.SettingsPageVM.Appearance == "dark" ? "light" : "dark"; }),
    };

    var palette = new CommandPaletteWindow(commands) { Owner = this };
    palette.ShowDialog();
}
```

---

## 9. 파일 변경 목록 (최종)

### 신규 파일 (5개)

| 파일 | 프로젝트 | 내용 |
|------|---------|------|
| `SettingsPageViewModel.cs` | GhostWin.App/ViewModels | 설정 ViewModel |
| `SettingsPageControl.xaml/.cs` | GhostWin.App/Controls | 설정 페이지 UserControl |
| `CommandPaletteWindow.xaml/.cs` | GhostWin.App | Command Palette Window |
| `CommandInfo.cs` | GhostWin.Core/Models | 명령 정보 record |

### 변경 파일 (5개)

| 파일 | 변경 내용 |
|------|----------|
| `MainWindow.xaml` | 기어 아이콘, SettingsPage 배치 (Column 4 겹침), PaneContainer InverseBoolToVis |
| `MainWindow.xaml.cs` | Ctrl+, / Esc / Ctrl+Shift+P 키, ShowCommandPalette, UpdateCellMetrics 구독, 포커스 복원 |
| `MainWindowViewModel.cs` | IsSettingsOpen, SettingsPageVM, OpenSettings/CloseSettings 커맨드, SettingsChanged에서 VM 갱신 |
| `SettingsService.cs` | _suppressWatcher 플래그, Save 시 설정, OnFileChanged에서 확인 |
| `App.xaml.cs` | SettingsChangedMessage에서 UpdateCellMetrics 호출 추가 |

**총계**: 신규 5 + 변경 5 = **10개 파일**

---

## 10. 검증 계획

| # | 시나리오 | 검증 |
|:-:|---------|------|
| T-1 | Ctrl+, → 설정 페이지 열림 | 수동 |
| T-2 | 기어 아이콘 클릭 → 설정 열림 | 수동 |
| T-3 | Esc → 터미널 복귀 + 포커스 복원 | 수동 (Space 키 동작 확인) |
| T-4 | 폰트 크기 슬라이더 → 터미널 반영 | 수동 |
| T-5 | 폰트 패밀리 변경 → 터미널 반영 | 수동 |
| T-6 | 테마 dark↔light 전환 | 수동 |
| T-7 | 알림 토글 → Phase 6 on/off | 수동 |
| T-8 | GUI 변경 → ghostwin.json 확인 | 파일 확인 |
| T-9 | JSON 수동 편집 → GUI 반영 | FileWatcher |
| T-10 | 자기 루프 미발생 확인 | CPU 모니터 |
| T-11 | Ctrl+Shift+P → Command Palette | 수동 |
| T-12 | Palette 검색 + Enter → 명령 실행 | 수동 |

---

## 11. 의도적 간소화

| # | 항목 | 간소화 | 근거 |
|:-:|------|-------|------|
| S-1 | 프로파일 시스템 | 미구현. 단일 설정만 | v2 범위 |
| S-2 | 컬러 스키마 에디터 | 미구현. dark/light 선택만 | v2 |
| S-3 | 키바인딩 충돌 자동 해제 | 경고 텍스트만 | v2 |
| S-4 | Mica 즉시 적용 | "restart required" 표시 | WPF 제한 |
| S-5 | 설정 검색 (Palette 내 설정 항목) | Command만 검색 | v2 |
| S-6 | 다국어 | 영어 하드코딩 | v2 |

---

## 참조

- **Plan**: `docs/01-plan/features/m12-settings-ui.plan.md`
- **AppSettings**: `src/GhostWin.Core/Models/AppSettings.cs`
- **SettingsService**: `src/GhostWin.Services/SettingsService.cs`
- **MainWindowViewModel**: `src/GhostWin.App/ViewModels/MainWindowViewModel.cs`

---

*M-12 Design v1.0 — Settings UI (2026-04-17)*
