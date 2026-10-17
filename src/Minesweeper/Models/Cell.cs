namespace Minesweeper.Models;

public enum CellState
{
    Covered,
    Revealed,
    Flagged
}

public class Cell
{
    public int Row { get; }
    public int Col { get; }
    public bool IsMine { get; set; }
    public int AdjacentMines { get; set; }
    public CellState State { get; set; } = CellState.Covered;

    /// <summary>游戏失败后标记：这面旗插在了安全格上</summary>
    public bool IsWrongFlag { get; set; }

    /// <summary>游戏失败后标记：这一格就是踩中的那颗雷</summary>
    public bool IsExploded { get; set; }

    public Cell(int row, int col)
    {
        Row = row;
        Col = col;
    }
}
