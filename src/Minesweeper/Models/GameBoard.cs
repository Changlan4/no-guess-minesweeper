namespace Minesweeper.Models;

public class GameBoard
{
    public int Rows { get; }
    public int Cols { get; }
    public int MineCount { get; }
    public Cell[,] Cells { get; }
    public bool GameOver { get; private set; }
    public bool Won { get; private set; }
    public bool FirstClickDone { get; private set; }
    public int FlaggedCount { get; private set; }

    /// <summary>踩中的那颗雷的坐标（用于失败时高亮），未踩雷时为 -1</summary>
    public int ExplodedRow { get; private set; } = -1;
    public int ExplodedCol { get; private set; } = -1;

    private bool _minesPlaced;

    public GameBoard(Difficulty difficulty)
    {
        Rows = difficulty.Rows;
        Cols = difficulty.Cols;
        MineCount = difficulty.MineCount;
        Cells = new Cell[Rows, Cols];

        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                Cells[r, c] = new Cell(r, c);
    }

    // ---- 揭示格子，返回所有新揭示的格子列表 ----
    public List<Cell> Reveal(int row, int col)
    {
        var revealed = new List<Cell>();

        if (GameOver) return revealed;

        var cell = Cells[row, col];
        if (cell.State == CellState.Flagged || cell.State == CellState.Revealed)
            return revealed;

        // 首击安全：首次点击后再放雷
        if (!_minesPlaced)
        {
            PlaceMines(row, col);
            _minesPlaced = true;
            FirstClickDone = true;
        }

        if (cell.IsMine)
        {
            cell.State = CellState.Revealed;
            revealed.Add(cell);
            GameOver = true;
            Won = false;
            ExplodedRow = row;
            ExplodedCol = col;
            return revealed;
        }

        // BFS 展开
        FloodReveal(row, col, revealed);

        if (CheckWin())
        {
            GameOver = true;
            Won = true;
        }

        return revealed;
    }

    // ---- 标旗/取消标旗 ----
    public bool ToggleFlag(int row, int col)
    {
        if (GameOver) return false;

        var cell = Cells[row, col];
        if (cell.State == CellState.Revealed) return false;

        if (cell.State == CellState.Flagged)
        {
            cell.State = CellState.Covered;
            FlaggedCount--;
        }
        else
        {
            cell.State = CellState.Flagged;
            FlaggedCount++;
        }
        return true;
    }

    // ---- 智能操作 (Chord) ----
    // 返回 (打开的安全格, 需要闪烁提醒的格子, 是否踩雷)
    // 不再自动标旗：不满足条件的格子交给玩家手动操作
    public (List<Cell> revealed, List<Cell> flashing, bool hitMine) Chord(int row, int col)
    {
        var revealed = new List<Cell>();
        var flashing = new List<Cell>();

        if (GameOver) return (revealed, flashing, false);

        var cell = Cells[row, col];
        if (cell.State != CellState.Revealed || cell.AdjacentMines == 0)
            return (revealed, flashing, false);

        var neighbors = GetNeighbors(row, col);
        int flaggedNeighbors = neighbors.Count(c => c.State == CellState.Flagged);
        int coveredNeighbors = neighbors.Count(c => c.State == CellState.Covered);

        if (flaggedNeighbors == cell.AdjacentMines && coveredNeighbors > 0)
        {
            // 旗子数 == 数字 → 翻开剩余未标记格（快速排雷）
            foreach (var n in neighbors)
            {
                if (n.State == CellState.Covered)
                {
                    var newRevealed = Reveal(n.Row, n.Col);
                    revealed.AddRange(newRevealed);

                    if (GameOver && !Won)
                        return (revealed, flashing, true);
                }
            }
        }
        else if (flaggedNeighbors < cell.AdjacentMines && coveredNeighbors > 0)
        {
            // 旗子不够 → 闪烁未标记格子，提醒玩家手动标旗
            foreach (var n in neighbors)
            {
                if (n.State == CellState.Covered)
                    flashing.Add(n);
            }
        }
        // flaggedNeighbors > cell.AdjacentMines → 旗子太多，不操作

        return (revealed, flashing, false);
    }

    // ---- 获取周围8格 ----
    public List<Cell> GetNeighbors(int row, int col)
    {
        var result = new List<Cell>();
        for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = row + dr, nc = col + dc;
                if (nr >= 0 && nr < Rows && nc >= 0 && nc < Cols)
                    result.Add(Cells[nr, nc]);
            }
        return result;
    }

    // ---- 揭示所有雷（游戏结束时） ----
    public void RevealAllMines()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var cell = Cells[r, c];
                if (cell.IsMine)
                {
                    if (cell.State != CellState.Flagged)
                        cell.State = CellState.Revealed;
                    if (r == ExplodedRow && c == ExplodedCol)
                        cell.IsExploded = true;
                }
                else if (cell.State == CellState.Flagged)
                {
                    // 插在安全格上的旗 —— 标出来，让玩家知道自己错在哪
                    cell.IsWrongFlag = true;
                }
            }
    }

    /// <summary>
    /// 按当前各格的实际状态重算 <see cref="FlaggedCount"/>。
    /// 供胜负收尾这类「直接改 Cell.State、不走 ToggleFlag」的批量操作收尾时使用，
    /// 否则计数会与盘面不一致（胜利后状态栏的「剩余雷数」就不会归零）。
    /// </summary>
    public void SyncFlaggedCount()
    {
        int count = 0;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (Cells[r, c].State == CellState.Flagged) count++;
        FlaggedCount = count;
    }

    // ---- 重开 ----
    public void Reset()
    {
        GameOver = false;
        Won = false;
        FirstClickDone = false;
        FlaggedCount = 0;
        ExplodedRow = -1;
        ExplodedCol = -1;
        _minesPlaced = false;

        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                Cells[r, c].IsMine = false;
                Cells[r, c].AdjacentMines = 0;
                Cells[r, c].State = CellState.Covered;
                Cells[r, c].IsWrongFlag = false;
                Cells[r, c].IsExploded = false;
            }
    }

    // ---- 内部：放雷 ----
    private void PlaceMines(int safeRow, int safeCol)
    {
        // 首击安全区：safeRow/safeCol 及其8邻居
        var safeZone = new HashSet<(int, int)> { (safeRow, safeCol) };
        foreach (var n in GetNeighbors(safeRow, safeCol))
            safeZone.Add((n.Row, n.Col));

        var candidates = new List<(int Row, int Col)>(Rows * Cols);
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (!safeZone.Contains((r, c))) candidates.Add((r, c));

        // 原来的「随机撒点 + 撞了就重试」在雷密度高时命中率会崩掉（极端情况直接死循环）。
        // 这里改成部分 Fisher-Yates 洗牌：均匀取样、线性时间、并且能明确报告摆不下。
        if (MineCount > candidates.Count)
            throw new InvalidOperationException(
                $"雷数 {MineCount} 超过首击安全区之外的可用格数 {candidates.Count}，无法布雷");

        var rng = Random.Shared;
        for (int i = 0; i < MineCount; i++)
        {
            int j = rng.Next(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            var (mr, mc) = candidates[i];
            Cells[mr, mc].IsMine = true;
        }

        // 计算数字
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (Cells[r, c].IsMine) continue;
                int count = 0;
                foreach (var n in GetNeighbors(r, c))
                    if (n.IsMine) count++;
                Cells[r, c].AdjacentMines = count;
            }
    }

    // ---- 内部：BFS 展开空白 ----
    private void FloodReveal(int row, int col, List<Cell> revealed)
    {
        var queue = new Queue<Cell>();
        var visited = new HashSet<Cell>();

        var start = Cells[row, col];
        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (cell.State == CellState.Revealed || cell.State == CellState.Flagged)
                continue;

            cell.State = CellState.Revealed;
            revealed.Add(cell);

            if (cell.AdjacentMines == 0)
            {
                foreach (var n in GetNeighbors(cell.Row, cell.Col))
                {
                    if (!visited.Contains(n) && n.State != CellState.Revealed)
                    {
                        visited.Add(n);
                        queue.Enqueue(n);
                    }
                }
            }
        }
    }

    // ---- 内部：判定胜利 ----
    private bool CheckWin()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                var cell = Cells[r, c];
                if (!cell.IsMine && cell.State != CellState.Revealed)
                    return false;
            }
        return true;
    }
}
