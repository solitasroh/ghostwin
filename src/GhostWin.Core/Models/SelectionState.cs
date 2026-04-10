namespace GhostWin.Core.Models;

/// <summary>
/// Selection mode for terminal text selection.
/// </summary>
public enum SelectionMode
{
    None,
    Cell,   // character-level drag selection
    Word,   // double-click word selection
    Line,   // triple-click line selection
}

/// <summary>
/// Terminal cell coordinate (0-based row and column).
/// </summary>
public readonly record struct CellCoord(int Row, int Col)
{
    /// <summary>
    /// Compare two coordinates in reading order (top-to-bottom, left-to-right).
    /// Returns negative if a comes before b, positive if after, 0 if equal.
    /// </summary>
    public static int Compare(CellCoord a, CellCoord b)
    {
        int rowCmp = a.Row.CompareTo(b.Row);
        return rowCmp != 0 ? rowCmp : a.Col.CompareTo(b.Col);
    }
}

/// <summary>
/// Normalized selection range with Start always before End in reading order.
/// </summary>
public readonly record struct SelectionRange(CellCoord Start, CellCoord End)
{
    /// <summary>
    /// Whether a given cell is within this range (inclusive).
    /// </summary>
    public bool Contains(int row, int col)
    {
        var cell = new CellCoord(row, col);
        return CellCoord.Compare(cell, Start) >= 0
            && CellCoord.Compare(cell, End) <= 0;
    }

    /// <summary>
    /// Whether this range spans at least one cell.
    /// </summary>
    public bool IsValid => CellCoord.Compare(Start, End) <= 0;
}

/// <summary>
/// Tracks mouse-driven text selection state for a terminal pane.
/// All mutations happen on the WndProc/UI thread (single-writer).
/// Render overlay reads via snapshot (CurrentRange).
///
/// Reference: Alacritty selection.rs, WezTerm selection.rs
/// </summary>
public class SelectionState
{
    private SelectionMode _mode = SelectionMode.None;
    private CellCoord _anchor;
    private CellCoord _end;

    /// <summary>Current selection mode.</summary>
    public SelectionMode Mode => _mode;

    /// <summary>Whether a selection is active (any mode except None).</summary>
    public bool IsActive => _mode != SelectionMode.None;

    /// <summary>
    /// Start a new selection at the given cell coordinate.
    /// </summary>
    public void Start(int row, int col, SelectionMode mode)
    {
        _mode = mode;
        _anchor = new CellCoord(row, col);
        _end = _anchor;
    }

    /// <summary>
    /// Extend the selection to a new cell coordinate (drag).
    /// </summary>
    public void Extend(int row, int col)
    {
        if (_mode == SelectionMode.None) return;
        _end = new CellCoord(row, col);
    }

    /// <summary>
    /// Clear the selection.
    /// </summary>
    public void Clear()
    {
        _mode = SelectionMode.None;
        _anchor = default;
        _end = default;
    }

    /// <summary>
    /// Get the normalized selection range (Start before End in reading order).
    /// Returns null if no selection is active.
    /// </summary>
    public SelectionRange? CurrentRange
    {
        get
        {
            if (_mode == SelectionMode.None) return null;

            var a = _anchor;
            var b = _end;

            // Normalize: start is always before end
            if (CellCoord.Compare(a, b) > 0)
                (a, b) = (b, a);

            return new SelectionRange(a, b);
        }
    }

    /// <summary>Raw anchor (un-normalized). For overlay calculation.</summary>
    public CellCoord Anchor => _anchor;

    /// <summary>Raw end (un-normalized). For overlay calculation.</summary>
    public CellCoord End => _end;
}
