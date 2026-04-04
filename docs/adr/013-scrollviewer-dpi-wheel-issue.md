# ADR-013: ScrollViewer Mouse Wheel DPI Issue

## Status

Open — deferred to future phase

## Context

Tab sidebar was refactored from ListView to StackPanel+ScrollViewer (10-agent 100% consensus). The refactoring eliminated 6 SelectionGuard workarounds and all ListView selection issues.

However, ScrollViewer mouse wheel scrolling does not work at DPI != 100% (125%, 150%).

## Problem

At non-100% DPI, mouse wheel events only reach XAML elements in a reduced area: `effective_area = actual_area / DPI_scale_factor`, anchored at window origin. Moving the window changes the effective area.

This is a known WinUI3/WinAppSDK bug class:
- [microsoft-ui-xaml#2101](https://github.com/microsoft/microsoft-ui-xaml/issues/2101) — XAML Islands mouse events scaled incorrectly on High-DPI
- [microsoft-ui-xaml#9231](https://github.com/microsoft/microsoft-ui-xaml/issues/9231) — Pointer mode only works in 25% of area at 200% scaling
- [microsoft-ui-xaml#9979](https://github.com/microsoft/microsoft-ui-xaml/issues/9979) — ScrollViewer+StackPanel mouse wheel regression in v1.6

## Attempted Solutions

| Approach | Result |
|----------|--------|
| XAML AddHandler(PointerWheelChangedEvent, handledEventsToo) on scroll_viewer_ | Failed — event not generated outside DPI-reduced hit area |
| XAML AddHandler on individual tab Grid items | Failed — same hit area limitation |
| Win32 WM_MOUSEWHEEL in InputWndProc (child HWND) | Failed — WM_MOUSEWHEEL goes to cursor window, not focus window |
| Win32 SetWindowSubclass on main HWND | Inconclusive — subclass installed but ChangeView may not execute correctly |
| WinAppSDK 1.6 → 1.8 upgrade | Bug persists in 1.8 |
| Various Background(Transparent) hit-test fixes | Partial — fixed hover-related issues but not core DPI problem |

## Root Cause

WinUI3 XAML input system has a DPI coordinate transformation bug in its hit-testing pipeline. When `WM_MOUSEWHEEL` arrives (screen coordinates in physical pixels), the XAML framework converts to element coordinates but incorrectly applies DPI scaling, causing the effective hit area to shrink by the scale factor.

Windows Terminal works around this by intercepting `WM_MOUSEWHEEL` at the Win32 HWND level (IslandWindow.cpp) and manually dispatching via `IMouseWheelListener` with DPI-corrected coordinates.

## Future Solutions

1. **Win32 HWND subclass + manual ChangeView**: Re-implement the WT pattern with proper HWND targeting and verified ChangeView execution. Need to identify which HWND in the WinUI3 hierarchy receives WM_MOUSEWHEEL.

2. **Custom scroll without ScrollViewer**: Use StackPanel with TranslateTransform.Y for manual scrolling. Clip via parent Grid. Bypass ScrollViewer entirely.

3. **WinAppSDK update monitoring**: Watch for fixes in future WinAppSDK releases (1.9+).

4. **ListView with SelectionMode::None**: Revert to ListView which has built-in DPI-correct scrolling, but disable selection system.

## Decision

Defer resolution. Current state:
- StackPanel architecture is correct and eliminates selection bugs
- Mouse wheel works at DPI 100%
- Keyboard tab navigation (Ctrl+Tab, Ctrl+1~9) works at all DPI scales
- Tab drag reorder works at all DPI scales (custom pointer drag)
- The DPI wheel issue affects only mouse wheel scrolling on the sidebar when >~10 tabs exist

## Consequences

- Users at non-100% DPI cannot scroll the tab sidebar with mouse wheel
- Keyboard shortcuts remain fully functional
- This is acceptable for the current development phase
- Will be addressed when a reliable workaround is identified

## References

- Windows Terminal IslandWindow.cpp WM_MOUSEWHEEL handling
- [WindowsAppSDK#5814](https://github.com/microsoft/WindowsAppSDK/issues/5814) — MRM resources.pri requirement (fixed via empty pri)
- [microsoft-ui-xaml#5520](https://github.com/microsoft/microsoft-ui-xaml/issues/5520) — DragStartingEventArgs DPI mismatch (same bug class)
