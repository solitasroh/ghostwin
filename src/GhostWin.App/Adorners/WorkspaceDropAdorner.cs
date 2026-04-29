using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace GhostWin.App.Adorners;

/// <summary>
/// M-16-D D-10: 1px horizontal line drawn over the sidebar ListBox while a
/// workspace drag is in progress. The line position is updated on every
/// DragOver — see MainWindow.OnSidebarDragOver. Color reuses the existing
/// M-16-A Accent.Primary token, keeping theme parity with other drop
/// indicators (drag handles, focus borders).
/// </summary>
public class WorkspaceDropAdorner : Adorner
{
    private readonly Pen _pen;

    public double LineY { get; set; }

    public WorkspaceDropAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
        var brush = (Brush?)Application.Current?.TryFindResource("Accent.Primary.Brush")
                    ?? Brushes.DodgerBlue;
        _pen = new Pen(brush, 2.0);
        if (_pen.CanFreeze) _pen.Freeze();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (AdornedElement is not FrameworkElement fe) return;
        drawingContext.DrawLine(_pen,
            new Point(0, LineY),
            new Point(fe.ActualWidth, LineY));
    }
}
