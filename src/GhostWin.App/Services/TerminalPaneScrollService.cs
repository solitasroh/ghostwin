using GhostWin.Core.Interfaces;

namespace GhostWin.App.Services;

public readonly record struct TerminalPaneScrollState(
    bool IsVisible,
    double Maximum,
    double Value,
    double ViewportSize,
    double LargeChange)
{
    public static TerminalPaneScrollState Hidden { get; } =
        new(IsVisible: false, Maximum: 0, Value: 0, ViewportSize: 0, LargeChange: 1);
}

public interface ITerminalPaneScrollService
{
    bool ForceContextMenu { get; }
    TerminalPaneScrollState GetState(uint sessionId);
    void ScrollTo(uint sessionId, double maximum, double newValue);
}

public sealed class TerminalPaneScrollService : ITerminalPaneScrollService
{
    private readonly IEngineService _engine;
    private readonly ISettingsService _settings;

    public TerminalPaneScrollService(IEngineService engine, ISettingsService settings)
    {
        _engine = engine;
        _settings = settings;
    }

    public bool ForceContextMenu => _settings.Current.Terminal.ForceContextMenu;

    public TerminalPaneScrollState GetState(uint sessionId)
    {
        if (!_engine.IsInitialized)
            return TerminalPaneScrollState.Hidden;

        var policy = _settings.Current.Terminal.Scrollbar ?? "system";
        if (policy == "never")
            return TerminalPaneScrollState.Hidden;

        var info = _engine.GetScrollbackInfo(sessionId);
        if (info is not { } sb)
            return TerminalPaneScrollState.Hidden;

        var shouldShow = policy == "always" || sb.ScrollbackRows > 0;
        if (!shouldShow)
            return TerminalPaneScrollState.Hidden;

        var maximum = (double)sb.ScrollbackRows;
        var offset = Math.Clamp(sb.ViewportOffsetFromBottom, 0, (int)sb.ScrollbackRows);
        var value = maximum - offset;
        var viewportRows = Math.Max(1, sb.ViewportRows);

        return new TerminalPaneScrollState(
            IsVisible: true,
            Maximum: maximum,
            Value: value,
            ViewportSize: viewportRows,
            LargeChange: viewportRows);
    }

    public void ScrollTo(uint sessionId, double maximum, double newValue)
    {
        if (!_engine.IsInitialized)
            return;

        var info = _engine.GetScrollbackInfo(sessionId);
        if (info is not { } sb)
            return;

        var targetOffset = (int)Math.Round(maximum - newValue);
        var currentOffset = sb.ViewportOffsetFromBottom;
        var delta = currentOffset - targetOffset;
        if (delta == 0)
            return;

        _engine.ScrollViewport(sessionId, delta);
    }
}
